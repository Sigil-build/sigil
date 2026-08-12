using System;
using System.Collections.Concurrent;
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
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Update;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Update;

/// <summary>
/// T12.6: the <c>/Update</c> runtime's PRODUCTION I/O seams
/// (<see cref="HttpUpdateResourceFetcher"/> + <see cref="SigilPackageDownloader"/>,
/// T12.3's <c>UpdateSeams</c> — the SAME classes <c>InstallSession.BuildUpdateRunner</c>
/// wires in production) driven end-to-end against a REAL local HTTPS server
/// (mirroring <c>HttpDownloadIntegrationTests</c>' pattern) and a REAL ECDSA P-256
/// signature generated in-test: fetch -&gt; parse (SIG0320) -&gt; verify (SIG0321) -&gt;
/// compare -&gt; download (real sha256 check) -&gt; "launch".
/// </summary>
/// <remarks>
/// The child-process launch uses a RECORDED-launch double
/// (<see cref="IChildInstallerLauncher"/>), not a real spawned Setup.exe — this
/// class proves the update DECISION table plus the real network + crypto +
/// checksum legs. A real child Setup.exe execution (the full cross-process
/// upgrade) is CI-VM-only; see the P12 job appended to
/// <c>.github/workflows/wrapper-vm-tests.yml</c>. The recording launcher reads
/// the downloaded file's bytes itself (before <c>UpdateRunner</c>'s <c>finally</c>
/// deletes it) and hashes them, so the test can assert the ACTUAL bytes that
/// reached disk match the manifest's sha256 — proof the download really
/// traveled over HTTP rather than the assertion trusting the downloader blindly.
/// </remarks>
public sealed class UpdateEndToEndTests
{
    private static UpgradeState Installed(string version) =>
        new(Found: true, InstalledVersion: version,
            PriorInstallDir: @"C:\Acme", PriorUninstallExe: @"C:\Acme\uninstall.exe",
            FoundScope: InstallScope.Machine);

    private static string Sha256Hex(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// <summary>
    /// Builds + signs a channel manifest JSON with an in-test-generated P-256
    /// keypair, mirroring <c>UpdateRunnerTests.SignedManifest</c> but pointed at a
    /// caller-supplied (real, local-server) <paramref name="packageUrl"/> so the
    /// download leg is real HTTP too, not just the manifest fetch.
    /// </summary>
    private static (byte[] ManifestBytes, string SignatureBase64, string PublicKeyBase64) BuildSignedManifest(
        string version, string packageUrl, string sha256, string? minFromVersion = null)
    {
        var minPart = minFromVersion is null ? string.Empty : $",\n  \"minFromVersion\": \"{minFromVersion}\"";
        // R13: freshness fields are required — minted now, valid for a week.
        var issued = DateTimeOffset.UtcNow;
        var json =
            "{\n" +
            "  \"schemaVersion\": 1,\n" +
            $"  \"issuedAt\": \"{issued:O}\",\n" +
            $"  \"expiresAt\": \"{issued.AddDays(7):O}\",\n" +
            "  \"sequence\": 1,\n" +
            $"  \"version\": \"{version}\",\n" +
            $"  \"packageUrl\": \"{packageUrl}\",\n" +
            $"  \"sha256\": \"{sha256}\"{minPart}\n" +
            "}";
        var bytes = Encoding.UTF8.GetBytes(json);
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKeyBase64 = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
        var signature = ecdsa.SignData(bytes, HashAlgorithmName.SHA256);
        return (bytes, Convert.ToBase64String(signature), publicKeyBase64);
    }

    /// <summary>
    /// Flips one character inside the (still-valid-JSON) <paramref name="version"/>
    /// string value of <paramref name="manifestBytes"/> — "flip a byte after
    /// signing" (brief wording verbatim). The result still PARSES as a well-formed
    /// channel manifest (SIG0320 does not fire) but its bytes no longer match what
    /// was signed, so verification (SIG0321) must fail.
    /// </summary>
    private static byte[] TamperVersionByte(byte[] manifestBytes, string version)
    {
        var json = Encoding.UTF8.GetString(manifestBytes);
        var idx = json.IndexOf(version, StringComparison.Ordinal);
        idx.Should().BeGreaterThanOrEqualTo(0, "the version string must be present verbatim in the manifest JSON");
        var tampered = (byte[])manifestBytes.Clone();
        var lastCharIndex = idx + version.Length - 1;
        var original = tampered[lastCharIndex];
        // Flip the last digit; wrap '9' back to '0' so it's always a different digit.
        tampered[lastCharIndex] = original == (byte)'9' ? (byte)'0' : (byte)(original + 1);
        return tampered;
    }

    private static IDisposable TrustServer(RoutingTlsServer server)
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

    private static UpdateRunner Runner(
        IChildInstallerLauncher launcher, Func<UpgradeState> installedStateProbe, List<string> log) =>
        new(
            fetcher: new HttpUpdateResourceFetcher(TimeSpan.FromSeconds(10)),
            downloader: new SigilPackageDownloader(TimeSpan.FromSeconds(30), maxAttempts: 2, report: null),
            launcher: launcher,
            installedStateProbe: installedStateProbe,
            report: (m, _) => log.Add(m));

    // ── 1. Happy path: newer version → real download (sha256-verified) + recorded launch ──

    [Fact]
    public async Task Newer_version_is_downloaded_verified_and_the_child_is_launched()
    {
        var packageBytes = Encoding.UTF8.GetBytes("this-is-the-real-update-package-payload");
        var packageSha256 = Sha256Hex(packageBytes);

        using var server = new RoutingTlsServer();
        using var _ = TrustServer(server);

        var (manifestBytes, sigB64, publicKey) = BuildSignedManifest("2.0.0", server.Url("/package.bin"), packageSha256);
        server.Map("/channel.json", manifestBytes);
        server.Map("/channel.json.sig", Encoding.UTF8.GetBytes(sigB64));
        server.Map("/package.bin", packageBytes);

        var launcher = new RecordingLauncher(exitCode: 0);
        var log = new List<string>();
        var runner = Runner(launcher, () => Installed("1.0.0"), log);
        var request = new UpdateRequest(
            ManifestUrl: server.Url("/channel.json"), SigningKey: publicKey, Channel: "stable",
            Scope: InstallScope.Machine, AppId: "com.acme.e2e", TempDirectory: Path.GetTempPath());

        var code = await runner.RunAsync(request, CancellationToken.None);

        code.Should().Be(0, "the recorded-launch double reports a clean exit");
        launcher.Called.Should().BeTrue("a strictly-newer channel version must be downloaded and launched");
        launcher.CapturedSha256.Should().Be(
            packageSha256, "the bytes that actually reached disk over real HTTP must match the manifest's sha256 — " +
            "proof the download + verify leg is real, not assumed");
        launcher.Args.Should().Equal(
            new[] { "/allusers", "/silent" }, "headless /Update forwards the scope flag + /silent by default");
        server.Hits("/channel.json").Should().Be(1);
        server.Hits("/channel.json.sig").Should().Be(1);
        server.Hits("/package.bin").Should().Be(1, "the package must be fetched over the real HTTP server exactly once");
        string.Join("\n", log).Should().Contain("newer version available");
    }

    // ── 2. Tampered manifest (flip a byte after signing) → SIG0321, no download, no launch ──

    [Fact]
    public async Task Tampered_manifest_is_hard_rejected_before_any_download_or_launch()
    {
        const string Version = "2.0.0";
        var packageBytes = Encoding.UTF8.GetBytes("would-be-downloaded-package");
        var packageSha256 = Sha256Hex(packageBytes);

        using var server = new RoutingTlsServer();
        using var _ = TrustServer(server);

        var (manifestBytes, sigB64, publicKey) = BuildSignedManifest(Version, server.Url("/package.bin"), packageSha256);
        var tamperedManifest = TamperVersionByte(manifestBytes, Version);

        // Serve the TAMPERED bytes but the ORIGINAL signature — the classic
        // "flip a byte after signing" tamper.
        server.Map("/channel.json", tamperedManifest);
        server.Map("/channel.json.sig", Encoding.UTF8.GetBytes(sigB64));
        server.Map("/package.bin", packageBytes);

        var launcher = new RecordingLauncher(exitCode: 0);
        var log = new List<string>();
        var runner = Runner(launcher, () => Installed("1.0.0"), log);
        var request = new UpdateRequest(
            ManifestUrl: server.Url("/channel.json"), SigningKey: publicKey, Channel: "stable",
            Scope: InstallScope.Machine, AppId: "com.acme.e2e", TempDirectory: Path.GetTempPath());

        var code = await runner.RunAsync(request, CancellationToken.None);

        code.Should().Be(InstallSession.UpdateManifestRejectedExitCode);
        launcher.Called.Should().BeFalse("a tampered manifest must never reach the launch stage");
        server.Hits("/package.bin").Should().Be(0, "a tampered (unverifiable) manifest must never trigger a package download");
        string.Join("\n", log).Should().Contain("SIG0321");
    }

    // ── 3. Tampered package (served bytes don't match the manifest's sha256) → no launch ──

    [Fact]
    public async Task Tampered_package_fails_the_real_checksum_check_and_is_never_launched()
    {
        var intendedBytes = Encoding.UTF8.GetBytes("the-genuine-package-bytes");
        var intendedSha256 = Sha256Hex(intendedBytes);
        // What the server actually serves at packageUrl is DIFFERENT — simulating a
        // corrupted/tampered package whose bytes no longer match the (correctly
        // signed, untouched) manifest's sha256.
        var servedBytes = Encoding.UTF8.GetBytes("a-tampered-substitute-payload-of-different-content");

        using var server = new RoutingTlsServer();
        using var _ = TrustServer(server);

        var (manifestBytes, sigB64, publicKey) =
            BuildSignedManifest("2.0.0", server.Url("/package.bin"), intendedSha256);
        server.Map("/channel.json", manifestBytes);
        server.Map("/channel.json.sig", Encoding.UTF8.GetBytes(sigB64));
        server.Map("/package.bin", servedBytes);

        var launcher = new RecordingLauncher(exitCode: 0);
        var log = new List<string>();
        var runner = Runner(launcher, () => Installed("1.0.0"), log);
        var request = new UpdateRequest(
            ManifestUrl: server.Url("/channel.json"), SigningKey: publicKey, Channel: "stable",
            Scope: InstallScope.Machine, AppId: "com.acme.e2e", TempDirectory: Path.GetTempPath());

        var code = await runner.RunAsync(request, CancellationToken.None);

        code.Should().Be(InstallSession.UpdateCheckFailedExitCode);
        launcher.Called.Should().BeFalse("the real sha256 mismatch must fail the download before any launch");
        server.Hits("/package.bin").Should().BeGreaterThanOrEqualTo(1, "the package WAS fetched — it just didn't verify");
        string.Join("\n", log).Should().Contain("download failed");
    }

    // ── 4. Up to date (channel not newer) → exit 0, no download ────────────────

    [Fact]
    public async Task Up_to_date_exits_zero_without_downloading()
    {
        var packageBytes = Encoding.UTF8.GetBytes("should-never-be-fetched");
        var packageSha256 = Sha256Hex(packageBytes);

        using var server = new RoutingTlsServer();
        using var _ = TrustServer(server);

        var (manifestBytes, sigB64, publicKey) =
            BuildSignedManifest("2.0.0", server.Url("/package.bin"), packageSha256);
        server.Map("/channel.json", manifestBytes);
        server.Map("/channel.json.sig", Encoding.UTF8.GetBytes(sigB64));
        server.Map("/package.bin", packageBytes);

        var launcher = new RecordingLauncher(exitCode: 0);
        var log = new List<string>();
        // Installed version is already the channel's version → "up to date".
        var runner = Runner(launcher, () => Installed("2.0.0"), log);
        var request = new UpdateRequest(
            ManifestUrl: server.Url("/channel.json"), SigningKey: publicKey, Channel: "stable",
            Scope: InstallScope.Machine, AppId: "com.acme.e2e", TempDirectory: Path.GetTempPath());

        var code = await runner.RunAsync(request, CancellationToken.None);

        code.Should().Be(0);
        launcher.Called.Should().BeFalse();
        server.Hits("/package.bin").Should().Be(0, "an up-to-date check must never touch the package endpoint");
        string.Join("\n", log).Should().Contain("up to date");
    }

    // ── Test doubles / fixtures ─────────────────────────────────────────────

    /// <summary>
    /// Records the exe path + args it was asked to "launch" and — critically —
    /// reads + hashes the file's bytes itself (before <c>UpdateRunner</c>'s
    /// <c>finally</c> block deletes it) so the test can assert on the REAL
    /// downloaded content rather than trusting the downloader's own report.
    /// Explicitly a recorded-launch double, not a real child process — see this
    /// class's remarks for why (a real Setup.exe launch is CI-VM-only).
    /// </summary>
    private sealed class RecordingLauncher : IChildInstallerLauncher
    {
        private readonly int _exitCode;

        public RecordingLauncher(int exitCode) => _exitCode = exitCode;

        public bool Called { get; private set; }
        public string? ExePath { get; private set; }
        public IReadOnlyList<string>? Args { get; private set; }
        public string? CapturedSha256 { get; private set; }

        public Task<int> RunAsync(string exePath, IReadOnlyList<string> args, CancellationToken ct)
        {
            Called = true;
            ExePath = exePath;
            Args = args;
            if (File.Exists(exePath))
            {
                CapturedSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(exePath))).ToLowerInvariant();
            }
            return Task.FromResult(_exitCode);
        }
    }

    /// <summary>
    /// Minimal HTTPS/1.1 server over <see cref="SslStream"/> with a self-signed
    /// certificate, routing by exact request path to a mapped byte payload (200)
    /// or 404 for anything unmapped. Mirrors
    /// <c>HttpDownloadIntegrationTests.TlsHttpServer</c> but generalized to serve
    /// three distinct resources (manifest / signature / package) from one server,
    /// with a per-path hit counter so a test can assert an endpoint was — or
    /// crucially, was NOT — ever requested.
    /// </summary>
    private sealed class RoutingTlsServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly X509Certificate2 _cert;
        private readonly Dictionary<string, byte[]> _routes = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, int> _hits = new(StringComparer.Ordinal);

        public RoutingTlsServer()
        {
            _cert = CreateSelfSignedCert();
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptLoopAsync();
        }

        public int Port { get; }
        public string Thumbprint => _cert.Thumbprint;
        public string Url(string path) => $"https://127.0.0.1:{Port}{path}";

        public void Map(string path, byte[] body) => _routes[path] = body;

        public int Hits(string path) => _hits.TryGetValue(path, out var n) ? n : 0;

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
                    var parts = requestLine.Split(' ');
                    var path = parts.Length > 1 ? parts[1] : "/";
                    // Drain headers.
                    while (!string.IsNullOrEmpty(await ReadLineAsync(ssl).ConfigureAwait(false))) { }

                    _hits.AddOrUpdate(path, 1, static (_, n) => n + 1);

                    byte[] body;
                    int status;
                    if (_routes.TryGetValue(path, out var mapped))
                    {
                        status = 200;
                        body = mapped;
                    }
                    else
                    {
                        status = 404;
                        body = Array.Empty<byte>();
                    }

                    var header = Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 {status} {(status == 200 ? "OK" : "NOT FOUND")}\r\n" +
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
