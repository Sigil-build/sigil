using System;
using System.Collections.Generic;
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

    // R16 contains http_download's `dest` to install_dir. Every case here
    // downloads into an OS temp directory, which no real install resolves as
    // install_dir, so the fixture declares the out-of-tree write with the
    // production per-step opt-out — the same one the web-installer stub's
    // synthesized download uses. Containment itself is exercised by
    // StepDestinationContainmentTests.
    private static InstallStep.HttpDownload Step(string url, string dest, string sha256, int? timeout = null, int? retries = null)
        => new("dl", url, dest, sha256, timeout, retries, When: null, OnFailure.Rollback)
        {
            AllowOutsideInstallDir = true,
        };

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

    // ── Register row R5: the gap between the checksum and the launch ──────────

    /// <summary>
    /// The decisive R5 case, through the real steps: <c>http_download</c> writes and
    /// verifies a binary, something replaces its bytes before the <c>run_program</c> that
    /// executes it, and the launch must be refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The attacker is stood in for by a <c>file_copy</c> step between the two — an
    /// ordinary catalog step, so no shell quoting or spawned helper can muddy what is
    /// being asserted. It occupies exactly the window register row R5 describes: the
    /// download handle is closed before the hash is compared and was never re-opened, so
    /// pre-fix the swapped bytes were simply executed, elevated.
    /// </para>
    /// <para>
    /// The substituted bytes are a valid image (the genuine payload plus one trailing
    /// byte — a PE overlay), so a launch would <em>succeed</em>: the refusal can only
    /// come from the re-verification, never from an unloadable file.
    /// </para>
    /// </remarks>
    [WindowsFact("launches a real child process")]
    public async Task A_downloaded_binary_swapped_before_run_program_is_never_launched()
    {
        var genuine = await File.ReadAllBytesAsync(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"));
        var swapped = new byte[genuine.Length + 1];
        genuine.CopyTo(swapped, 0);

        using var server = new TlsHttpServer((_, _) => (200, genuine, 0));
        using var _ = TrustServer(server);
        using var tmp = new TempDir();

        // Pin the staging siting to a scratch directory. Without this, {staging_dir}
        // resolves through the production path, and on an ELEVATED host — which CI is —
        // this test would create a directory in the real %ProgramData% and execute a copy
        // of cmd.exe out of it.
        using var scratch = new TempDir();
        using var siting = SecureStaging.UseSitingForTesting(scratch.Path);

        var ctx = new StepContext(new Dictionary<string, object?>(StringComparer.Ordinal));
        var stagingDir = ctx.ResolvePath("{staging_dir}");
        var dest = Path.Combine(stagingDir, "Acme-3.2.0-x64-Setup.exe");
        stagingDir.Should().StartWith(scratch.Path, "no test may stage into a real %ProgramData% path");

        // The attacker's copy carries the SAME file name, so copying it into the staging
        // directory overwrites exactly the file that is about to be launched.
        var attackerDir = Path.Combine(tmp.Path, "attacker");
        Directory.CreateDirectory(attackerDir);
        await File.WriteAllBytesAsync(
            Path.Combine(attackerDir, "Acme-3.2.0-x64-Setup.exe"), swapped);

        var result = await new InstallEngine().RunAsync(
            new InstallStep[]
            {
                Step(server.Url("/pkg"), dest, Sha256Hex(genuine)),
                new InstallStep.FileCopy(
                    "swap",
                    Path.Combine(attackerDir, "Acme-3.2.0-x64-Setup.exe"),
                    stagingDir,
                    Overwrite: true,
                    When: null,
                    OnFailure.Fail),
                new InstallStep.RunProgram(
                    "launch", dest, new[] { "/c", "exit", "/b", "0" }, Wait: true, Cwd: null,
                    ExpectedExitCodes: new[] { 0 }, TimeoutSeconds: 30, When: null, OnFailure.Fail),
            },
            ctx);

        result.Success.Should().BeFalse(
            "a binary whose bytes changed after they were verified must never be executed — the sha256 " +
            "protected the download, not the execution");
        result.Error.Should().Contain("no longer matches its verified sha256");
        result.Error.Should().Contain("refusing to run it");
    }

    /// <summary>
    /// The positive control for the case above: with nothing tampering, the identical
    /// download-then-run pair succeeds. Without it, "refused" could just as well mean
    /// "this shape never worked".
    /// </summary>
    [WindowsFact("launches a real child process")]
    public async Task An_untampered_downloaded_binary_is_verified_again_and_launched()
    {
        var genuine = await File.ReadAllBytesAsync(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"));

        using var server = new TlsHttpServer((_, _) => (200, genuine, 0));
        using var _ = TrustServer(server);

        // See the test above: the siting is pinned so this never stages into, or executes
        // from, a real %ProgramData% path on an elevated runner.
        using var scratch = new TempDir();
        using var siting = SecureStaging.UseSitingForTesting(scratch.Path);

        var ctx = new StepContext(new Dictionary<string, object?>(StringComparer.Ordinal));
        var stagingDir = ctx.ResolvePath("{staging_dir}");
        stagingDir.Should().StartWith(scratch.Path, "no test may stage into a real %ProgramData% path");
        var dest = Path.Combine(stagingDir, "Acme-3.2.0-x64-Setup.exe");

        var result = await new InstallEngine().RunAsync(
            new InstallStep[]
            {
                Step(server.Url("/pkg"), dest, Sha256Hex(genuine)),
                new InstallStep.RunProgram(
                    "launch", dest, new[] { "/c", "exit", "/b", "0" }, Wait: true, Cwd: null,
                    ExpectedExitCodes: new[] { 0 }, TimeoutSeconds: 30, When: null, OnFailure.Fail),
            },
            ctx);

        result.Success.Should().BeTrue(
            "holding the verified handle across the launch must not block the launch — that is why the " +
            "sharing mode is FileShare.Read and not FileShare.None");
        Directory.Exists(Path.GetDirectoryName(dest)!).Should().BeFalse(
            "the engine releases {staging_dir} when the run ends, so the downloaded package is not left behind");
    }

    /// <summary>
    /// The other half of R5's residual: <c>SigilDownloader</c> opened its destination
    /// with <see cref="FileMode.Create"/>, which opens an <em>existing</em> name and
    /// truncates it. A hardlink planted at a predictable destination therefore had its
    /// <b>target</b> rewritten — from an elevated process, an arbitrary-file-write
    /// primitive.
    /// </summary>
    /// <remarks>
    /// The victim here is a scratch file in a throwaway directory, never a real system
    /// file: the mechanism is identical, and CI runs elevated, where aiming this at
    /// anything real would damage the runner.
    /// </remarks>
    [WindowsFact("NTFS hard links")]
    public async Task A_hardlink_planted_at_the_destination_does_not_get_its_target_rewritten()
    {
        var body = Encoding.UTF8.GetBytes("the-downloaded-payload");
        using var server = new TlsHttpServer((_, _) => (200, body, 0));
        using var _ = TrustServer(server);
        using var tmp = new TempDir();

        // The victim: a file the elevated process may write but must not be tricked into
        // rewriting. A scratch file stands in for the real target of such an attack.
        var victim = Path.Combine(tmp.Path, "victim.dat");
        var victimBytes = Encoding.UTF8.GetBytes("bytes that must survive the download");
        await File.WriteAllBytesAsync(victim, victimBytes);

        // The attacker pre-plants a second NAME for the victim at the download's
        // destination. Creating a hard link needs no privilege on NTFS.
        var dest = Path.Combine(tmp.Path, "payload.bin");
        CreateHardLink(dest, victim);

        var result = await new InstallEngine().RunAsync(
            new[] { Step(server.Url("/f"), dest, Sha256Hex(body)) },
            new StepContext(new Dictionary<string, object?>(StringComparer.Ordinal)));

        result.Success.Should().BeTrue();
        File.ReadAllBytes(dest).Should().Equal(body, "the download still lands at its destination");
        File.ReadAllBytes(victim).Should().Equal(
            victimBytes,
            "the download must unlink the planted NAME and create a fresh file, never write through it — " +
            "FileMode.Create would have truncated the link's target instead");
    }

    /// <summary>
    /// Create a hard link at <paramref name="link"/> pointing at <paramref name="target"/>
    /// via <c>mklink /H</c>, which needs no privilege on NTFS. Asserts the link really
    /// exists and really aliases the target — a test that silently degraded to "no link"
    /// would pass vacuously, proving nothing at all.
    /// </summary>
    private static void CreateHardLink(string link, string target)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("mklink");
        psi.ArgumentList.Add("/H");
        psi.ArgumentList.Add(link);
        psi.ArgumentList.Add(target);

        using var process = System.Diagnostics.Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        process.ExitCode.Should().Be(0, $"mklink /H must succeed: {stdout} {stderr}");
        File.Exists(link).Should().BeTrue("precondition: the hard link must actually exist");
        File.ReadAllBytes(link).Should().Equal(
            File.ReadAllBytes(target),
            "precondition: the link must genuinely alias the victim — otherwise this test proves nothing");
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
