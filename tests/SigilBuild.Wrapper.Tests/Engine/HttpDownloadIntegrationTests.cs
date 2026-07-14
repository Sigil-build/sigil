using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Cli;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Tests.Helpers;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Engine;

/// <summary>
/// P4 (gap G5): the http_download step against a local HTTPS server with a
/// self-signed certificate (trusted just for the test via the injectable
/// <see cref="SigilHttpClient"/> seam). Covers success + hash verification,
/// checksum-mismatch rollback, timeout-then-retry, and retries-exhausted → a
/// non-zero silent exit.
/// </summary>
public sealed class HttpDownloadIntegrationTests
{
    private static string Sha256Hex(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static InstallStep.HttpDownload Step(string url, string dest, string sha256, int? timeout = null, int? retries = null)
        => new("dl", url, dest, sha256, timeout, retries, When: null, OnFailure.Rollback);

    private static IDisposable TrustServer(TlsHttpServer server)
    {
        var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                    cert is X509Certificate2 c && c.Thumbprint == server.Thumbprint,
            },
        };
        var client = new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        return SigilHttpClient.UseForTesting(client);
    }

    [Fact]
    public async Task Success_downloads_the_file_with_a_matching_hash()
    {
        var body = Encoding.UTF8.GetBytes("hello-download-payload");
        using var server = new TlsHttpServer((_, _) => (200, body, 0));
        using var _ = TrustServer(server);
        using var tmp = new TempDir();
        var dest = Path.Combine(tmp.Path, "payload.bin");

        var result = await new InstallEngine().RunAsync(
            new[] { Step(server.Url("/f"), dest, Sha256Hex(body)) }, StepContext.Empty);

        result.Success.Should().BeTrue();
        File.Exists(dest).Should().BeTrue();
        File.ReadAllBytes(dest).Should().Equal(body);
    }

    [Fact]
    public async Task Checksum_mismatch_fails_and_rollback_removes_the_file()
    {
        var body = Encoding.UTF8.GetBytes("some-bytes");
        using var server = new TlsHttpServer((_, _) => (200, body, 0));
        using var _ = TrustServer(server);
        using var tmp = new TempDir();
        var dest = Path.Combine(tmp.Path, "payload.bin");

        var result = await new InstallEngine().RunAsync(
            new[] { Step(server.Url("/f"), dest, "0000000000000000000000000000000000000000000000000000000000000000") },
            StepContext.Empty);

        result.Success.Should().BeFalse();
        File.Exists(dest).Should().BeFalse("a checksum mismatch must roll back the downloaded file");
    }

    [Fact]
    public async Task Timeout_on_first_attempt_then_retry_succeeds()
    {
        var body = Encoding.UTF8.GetBytes("retry-succeeds");
        // First received request stalls past the 1 s timeout; the retry is served fast.
        using var server = new TlsHttpServer((attempt, _) => (200, body, attempt == 1 ? 3000 : 0));
        using var _ = TrustServer(server);
        using var tmp = new TempDir();
        var dest = Path.Combine(tmp.Path, "payload.bin");

        var result = await new InstallEngine().RunAsync(
            new[] { Step(server.Url("/f"), dest, Sha256Hex(body), timeout: 1, retries: 2) }, StepContext.Empty);

        result.Success.Should().BeTrue("the second attempt is served within the timeout");
        File.ReadAllBytes(dest).Should().Equal(body);
        server.RequestCount.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task Retries_exhausted_fails_the_silent_install_with_nonzero_exit()
    {
        // Server always 503 → transient → all attempts fail.
        using var server = new TlsHttpServer((_, _) => (503, Array.Empty<byte>(), 0));
        using var _ = TrustServer(server);
        using var tmp = new TempDir();
        var dest = Path.Combine(tmp.Path, "payload.bin");

        var blob = new WrapperBlob(
            AppId: "com.acme.p4-" + Guid.NewGuid().ToString("N"),
            Parameters: Array.Empty<ParameterDefinition>(),
            InstallSteps: new InstallStep[] { Step(server.Url("/f"), dest, "abc", retries: 1) },
            PreInstall: Array.Empty<InstallStep>(),
            PostInstall: Array.Empty<InstallStep>(),
            UpdateSteps: Array.Empty<InstallStep>());
        var parsed = CommandLineParser.Parse(new[] { "/silent" }, blob.Parameters);
        var session = InstallSession.ForTesting(blob, parsed);

        var code = await session.RunHeadlessAsync(new StringWriter(), new StringWriter());

        code.Should().Be(1, "retries exhausted → the silent install exits non-zero");
        File.Exists(dest).Should().BeFalse("rollback removes any partial download");
        server.RequestCount.Should().Be(2, "1 initial attempt + 1 retry");
    }

    /// <summary>
    /// Minimal HTTPS/1.1 server over <see cref="SslStream"/> with a self-signed
    /// certificate, for exercising the download step end to end. The handler maps
    /// (received-request-number, path) → (status, body, delay-ms).
    /// </summary>
    private sealed class TlsHttpServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly X509Certificate2 _cert;
        private readonly Func<int, string, (int Status, byte[] Body, int DelayMs)> _handler;
        private int _requests;

        public TlsHttpServer(Func<int, string, (int, byte[], int)> handler)
        {
            _handler = handler;
            _cert = CreateSelfSignedCert();
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptLoopAsync();
        }

        public int Port { get; }
        public int RequestCount => Volatile.Read(ref _requests);
        public string Thumbprint => _cert.Thumbprint;
        public string Url(string path) => $"https://127.0.0.1:{Port}{path}";

        private async Task AcceptLoopAsync()
        {
            while (true)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch { return; } // listener stopped
                _ = HandleAsync(client);
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
#pragma warning disable CA1031 // test server: swallow all per-connection errors
            try
            {
                using (client)
                await using (var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false))
                {
                    await ssl.AuthenticateAsServerAsync(_cert, clientCertificateRequired: false,
                        checkCertificateRevocation: false).ConfigureAwait(false);

                    var requestLine = await ReadLineAsync(ssl).ConfigureAwait(false);
                    var attempt = Interlocked.Increment(ref _requests);
                    var parts = requestLine.Split(' ');
                    var path = parts.Length > 1 ? parts[1] : "/";
                    // Drain headers.
                    while (!string.IsNullOrEmpty(await ReadLineAsync(ssl).ConfigureAwait(false))) { }

                    var (status, body, delayMs) = _handler(attempt, path);
                    if (delayMs > 0)
                    {
                        await Task.Delay(delayMs).ConfigureAwait(false);
                    }

                    var header = Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 {status} {(status == 200 ? "OK" : "ERR")}\r\n" +
                        $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                    await ssl.WriteAsync(header).ConfigureAwait(false);
                    if (body.Length > 0)
                    {
                        await ssl.WriteAsync(body).ConfigureAwait(false);
                    }
                    await ssl.FlushAsync().ConfigureAwait(false);
                }
            }
            catch { /* client gone / handshake aborted — ignore */ }
#pragma warning restore CA1031
        }

        private static async Task<string> ReadLineAsync(SslStream ssl)
        {
            var sb = new StringBuilder();
            var one = new byte[1];
            while (true)
            {
                var n = await ssl.ReadAsync(one.AsMemory(0, 1)).ConfigureAwait(false);
                if (n == 0) break;
                if (one[0] == (byte)'\n') break;
                if (one[0] != (byte)'\r') sb.Append((char)one[0]);
            }
            return sb.ToString();
        }

        private static X509Certificate2 CreateSelfSignedCert()
        {
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            req.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
            using var ephemeral = req.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
            // Export + reimport so the private key is usable by SslStream on Windows.
            var pfx = ephemeral.Export(X509ContentType.Pfx);
            return X509CertificateLoader.LoadPkcs12(pfx, password: null, X509KeyStorageFlags.Exportable);
        }

        public void Dispose()
        {
            _listener.Stop();
            _cert.Dispose();
        }
    }
}
