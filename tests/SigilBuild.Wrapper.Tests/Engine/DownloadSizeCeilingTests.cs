namespace SigilBuild.Wrapper.Tests.Engine;

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
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Tests.Helpers;
using SigilBuild.Wrapper.Update;
using Xunit;

/// <summary>
/// Register row R10 — no size cap on any download, and the channel manifest fully
/// buffered before its signature is checked.
/// </summary>
/// <remarks>
/// <para>
/// Two ceilings, tested separately because they defend different things:
/// </para>
/// <list type="number">
///   <item><description>
///   <b><see cref="SigilDownloader"/>'s file ceiling.</b> <c>Content-Length</c> was read
///   only to compute a progress percentage, and the read loop had no cap at all. Both
///   halves of the fix are asserted, and they are told apart by <em>whether the
///   destination file was ever created</em>: the declared-oversize refusal happens before
///   the destination is opened, the mid-stream abort necessarily after.
///   </description></item>
///   <item><description>
///   <b><see cref="HttpUpdateResourceFetcher"/>'s pre-authentication ceiling.</b> That
///   buffer is filled before <c>ChannelManifestVerifier.Verify</c> runs, so every byte
///   in it is accepted on an unauthenticated party's say-so. These tests name no type
///   and no member that did not already exist, so the whole class of them can be dropped
///   onto the parent commit unchanged to watch them fail.
///   </description></item>
/// </list>
/// <para>
/// <b>Elevation.</b> Nothing here reads a token, an ACL, or a signature: the assertions
/// are over HTTP framing and stream accounting, which are identical on an elevated and
/// an unelevated host. Every test asserts the same thing, and takes the same branch, in
/// both cases. Nothing is written outside a <see cref="TempDir"/> and nothing is listened
/// on but an ephemeral loopback port.
/// </para>
/// </remarks>
public sealed class DownloadSizeCeilingTests
{
    private const int Ceiling = 64 * 1024;

    /// <summary>
    /// A channel-manifest body far above any plausible one (2 MiB against a real
    /// manifest's few hundred bytes). A literal, not
    /// <c>HttpUpdateResourceFetcher.MaxResourceBytes</c>: naming the constant the fix
    /// introduced would stop these tests compiling on the parent commit, and being able
    /// to run them there unchanged is the point of writing them this way.
    /// </summary>
    private const int OversizedResource = 2 * 1024 * 1024;

    private static string Sha256Hex(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static byte[] Filler(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)(i & 0xFF);
        }
        return bytes;
    }

    // ── 1. SigilDownloader: the declared oversize, refused before the body ─────

    [Fact]
    public async Task A_declared_content_length_above_the_ceiling_is_refused_before_the_body_is_read()
    {
        var body = Filler(Ceiling * 4);
        using var server = new SizedTlsServer(Body.WithContentLength(body));
        using var trusted = TrustServer(server);
        using var tmp = new TempDir();
        var dest = Path.Combine(tmp.Path, "payload.bin");

        var result = await SigilDownloader.DownloadVerifiedAsync(
            server.Url("/big"), dest, Sha256Hex(body),
            TimeSpan.FromSeconds(30), maxAttempts: 3, maxBytes: Ceiling, report: null, CancellationToken.None);

        result.Status.Should().Be(SigilDownloader.DownloadStatus.TooLarge);
        result.Error.Should().Contain("declared a body of");
        File.Exists(dest).Should().BeFalse(
            "a declared oversize must be refused before the destination file is even created — that is what " +
            "makes it the cheap half");
        server.RequestCount.Should().Be(1, "an oversized body is permanent; retrying it would amplify the transfer");
    }

    // ── 2. SigilDownloader: the server that declares nothing and drips ─────────

    /// <summary>
    /// The half that actually defends anything. A response framed by connection-close
    /// carries no <c>Content-Length</c>, so the up-front check never fires — and the
    /// per-request timeout does not bound it either, because a body that keeps arriving
    /// keeps the request alive. Only a metered read loop stops it.
    /// </summary>
    [Fact]
    public async Task A_body_with_no_declared_length_is_aborted_once_it_passes_the_ceiling()
    {
        var body = Filler(Ceiling * 16);
        using var server = new SizedTlsServer(Body.Undeclared(body));
        using var trusted = TrustServer(server);
        using var tmp = new TempDir();
        var dest = Path.Combine(tmp.Path, "payload.bin");

        var result = await SigilDownloader.DownloadVerifiedAsync(
            server.Url("/drip"), dest, Sha256Hex(body),
            TimeSpan.FromSeconds(30), maxAttempts: 3, maxBytes: Ceiling, report: null, CancellationToken.None);

        result.Status.Should().Be(SigilDownloader.DownloadStatus.TooLarge);
        result.Error.Should().Contain("no Content-Length",
            "the message must say the server declared nothing — otherwise this could be mistaken for the " +
            "up-front check firing");
        new FileInfo(dest).Length.Should().BeLessThanOrEqualTo(
            Ceiling,
            "the abort is checked before the write, so not one byte past the ceiling reaches the disk — " +
            "without it the whole body lands, ceiling or no ceiling");
        server.RequestCount.Should().Be(1);
    }

    // ── 3. Positive control: the ceiling does not break a legitimate download ──

    /// <summary>
    /// Without this, "refused" above could equally mean "this shape never worked". A body
    /// of exactly <see cref="Ceiling"/> bytes is at the boundary and must still succeed.
    /// </summary>
    [Fact]
    public async Task A_body_of_exactly_the_ceiling_still_downloads_and_verifies()
    {
        var body = Filler(Ceiling);
        using var server = new SizedTlsServer(Body.WithContentLength(body));
        using var trusted = TrustServer(server);
        using var tmp = new TempDir();
        var dest = Path.Combine(tmp.Path, "payload.bin");

        var result = await SigilDownloader.DownloadVerifiedAsync(
            server.Url("/exact"), dest, Sha256Hex(body),
            TimeSpan.FromSeconds(30), maxAttempts: 1, maxBytes: Ceiling, report: null, CancellationToken.None);

        result.Status.Should().Be(SigilDownloader.DownloadStatus.Ok);
        File.ReadAllBytes(dest).Should().Equal(body);
    }

    // ── 4. The pre-authentication buffer (the higher-value half) ───────────────

    /// <summary>
    /// The decisive negative test for R10, and the one that compiles unchanged on the
    /// parent commit: <see cref="HttpUpdateResourceFetcher.FetchAsync"/>'s signature is
    /// untouched by the fix. On the parent it buffers the whole undeclared body and
    /// reports success; here it must refuse.
    /// </summary>
    [Fact]
    public async Task The_channel_manifest_fetch_aborts_an_undeclared_oversized_body()
    {
        var body = Filler(OversizedResource);
        using var server = new SizedTlsServer(Body.Undeclared(body));
        using var trusted = TrustServer(server);

        var fetcher = new HttpUpdateResourceFetcher(TimeSpan.FromSeconds(30));
        var result = await fetcher.FetchAsync(server.Url("/stable.json"), CancellationToken.None);

        result.Success.Should().BeFalse(
            "this buffer is filled BEFORE ChannelManifestVerifier.Verify runs, so an unbounded read here is " +
            "reachable by an unauthenticated party");
        result.Bytes.Should().BeNull();
        result.Error.Should().Contain("streamed more than");
    }

    [Fact]
    public async Task The_channel_manifest_fetch_refuses_a_declared_oversized_body()
    {
        var body = Filler(OversizedResource);
        using var server = new SizedTlsServer(Body.WithContentLength(body));
        using var trusted = TrustServer(server);

        var fetcher = new HttpUpdateResourceFetcher(TimeSpan.FromSeconds(30));
        var result = await fetcher.FetchAsync(server.Url("/stable.json"), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("above the");
    }

    /// <summary>
    /// Positive control for the two above: a real-sized channel manifest still comes back
    /// byte-for-byte. The verification is over the exact fetched bytes, so a fetch that
    /// silently truncated or re-encoded would break every signature check.
    /// </summary>
    [Fact]
    public async Task A_normal_sized_channel_manifest_is_fetched_unchanged()
    {
        var body = Encoding.UTF8.GetBytes(
            "{\"schemaVersion\":1,\"version\":\"2.0.0\",\"packageUrl\":\"https://x/Setup.exe\",\"sha256\":\"" +
            new string('a', 64) + "\"}");
        using var server = new SizedTlsServer(Body.WithContentLength(body));
        using var trusted = TrustServer(server);

        var fetcher = new HttpUpdateResourceFetcher(TimeSpan.FromSeconds(30));
        var result = await fetcher.FetchAsync(server.Url("/stable.json"), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Bytes.Should().Equal(body);
    }

    // ── 5. The ordering claim, end to end through the update runner ────────────

    /// <summary>
    /// Makes the "pre-authentication" claim observable rather than asserted: the runner
    /// is given the REAL fetcher pointed at an origin that drips, and a signing key it
    /// would need in order to verify anything. It never gets that far — the fetch is
    /// refused, and no launch happens.
    /// </summary>
    [Fact]
    public async Task An_oversized_channel_manifest_stops_the_update_before_any_verification()
    {
        using var server = new SizedTlsServer(Body.Undeclared(Filler(OversizedResource)));
        using var trusted = TrustServer(server);
        using var tmp = new TempDir();

        var launcher = new NeverLauncher();
        var log = new System.Collections.Generic.List<string>();
        var runner = new UpdateRunner(
            new HttpUpdateResourceFetcher(TimeSpan.FromSeconds(30)),
            new NeverDownloader(),
            launcher,
            () => new UpgradeState(
                Found: true, InstalledVersion: "1.0.0", PriorInstallDir: @"C:\Acme",
                PriorUninstallExe: @"C:\Acme\uninstall.exe", FoundScope: InstallScope.Machine),
            (m, _) => log.Add(m),
            new UpdateFixtures.InMemorySequenceStore());

        var code = await runner.RunAsync(
            new UpdateRequest(
                ManifestUrl: server.Url("/stable.json"),
                SigningKey: Convert.ToBase64String(
                    ECDsa.Create(ECCurve.NamedCurves.nistP256).ExportSubjectPublicKeyInfo()),
                Channel: "stable", Scope: InstallScope.Machine, AppId: "com.acme.Studio",
                TempDirectory: tmp.Path),
            CancellationToken.None);

        code.Should().Be(InstallSession.UpdateCheckFailedExitCode);
        launcher.Called.Should().BeFalse();
        string.Join("\n", log).Should().Contain("streamed more than");
    }

    private sealed class NeverLauncher : IChildInstallerLauncher
    {
        public bool Called { get; private set; }

        public Task<int> RunAsync(string exePath, System.Collections.Generic.IReadOnlyList<string> args, CancellationToken ct)
        {
            Called = true;
            return Task.FromResult(0);
        }
    }

    private sealed class NeverDownloader : IUpdatePackageDownloader
    {
        public Task<UpdatePackageDownloadResult> DownloadAsync(
            string url, string destination, string sha256, CancellationToken ct)
            => throw new InvalidOperationException("the update must never reach the download stage in this test");
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    /// <summary>
    /// How the test origin frames its response body: with an honest
    /// <c>Content-Length</c>, or with none at all (connection-close framing), which is
    /// how a real slow-drip origin evades any header-based check.
    /// </summary>
    private readonly record struct Body(byte[] Bytes, bool DeclareLength)
    {
        public static Body WithContentLength(byte[] bytes) => new(bytes, true);

        public static Body Undeclared(byte[] bytes) => new(bytes, false);
    }

    private static IDisposable TrustServer(SizedTlsServer server)
    {
        var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                    cert is X509Certificate2 c && c.Thumbprint == server.Thumbprint,
            },
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        return SigilHttpClient.UseForTesting(client);
    }

    /// <summary>
    /// Minimal HTTPS/1.1 origin over <see cref="SslStream"/> — the same shape as the one
    /// in <c>HttpDownloadIntegrationTests</c>, duplicated on purpose (as
    /// <c>StagedExecutionTests</c> does) so this file can be dropped onto the parent
    /// commit unchanged, with the one addition this row needs: the ability to answer
    /// <b>without</b> a <c>Content-Length</c>.
    /// </summary>
    private sealed class SizedTlsServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly X509Certificate2 _cert;
        private readonly Body _body;
        private int _requests;

        public SizedTlsServer(Body body)
        {
            _body = body;
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
#pragma warning disable CA1031 // test origin: swallow all per-connection errors
            try
            {
                using (client)
                await using (var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false))
                {
                    await ssl.AuthenticateAsServerAsync(_cert, clientCertificateRequired: false,
                        checkCertificateRevocation: false).ConfigureAwait(false);

                    _ = await ReadLineAsync(ssl).ConfigureAwait(false);
                    Interlocked.Increment(ref _requests);
                    while (!string.IsNullOrEmpty(await ReadLineAsync(ssl).ConfigureAwait(false))) { }

                    var header = _body.DeclareLength
                        ? $"HTTP/1.1 200 OK\r\nContent-Length: {_body.Bytes.Length}\r\nConnection: close\r\n\r\n"
                        // No Content-Length and no Transfer-Encoding: HTTP/1.1 framing by
                        // connection close. The client cannot know how much is coming.
                        : "HTTP/1.1 200 OK\r\nConnection: close\r\n\r\n";
                    await ssl.WriteAsync(Encoding.ASCII.GetBytes(header)).ConfigureAwait(false);

                    // Written in chunks so the client sees a genuine stream rather than one
                    // atomic buffer — a mid-stream abort has to be able to happen mid-stream.
                    const int Chunk = 16 * 1024;
                    for (var offset = 0; offset < _body.Bytes.Length; offset += Chunk)
                    {
                        var n = Math.Min(Chunk, _body.Bytes.Length - offset);
                        await ssl.WriteAsync(_body.Bytes.AsMemory(offset, n)).ConfigureAwait(false);
                        await ssl.FlushAsync().ConfigureAwait(false);
                    }
                }
            }
            catch { /* client gone / aborted mid-body — expected in the refusal tests */ }
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
