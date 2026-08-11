namespace SigilBuild.Wrapper.Tests.Engine;

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
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Tests.Helpers;
using SigilBuild.Wrapper.Update;
using Xunit;

/// <summary>
/// Register row R12, end to end through the two runners that stage and launch a
/// downloaded binary: the prerequisite runner and the <c>/Update</c> runner.
/// </summary>
/// <remarks>
/// <para>
/// R12's two halves are asserted separately, because a fix for one is not a fix for
/// the other:
/// </para>
/// <list type="number">
///   <item><description>
///   <b>No handle is held.</b> Both runners downloaded to <c>%TEMP%\…-{Guid}.exe</c>,
///   let the file close, and then launched it. The <c>Launcher</c> /
///   <c>IChildInstallerLauncher</c> seam stands in for the attacker who wins the
///   directory-change race: it is handed the very path about to be executed and
///   tries to overwrite and delete it. Before the fix both succeed; after it, the
///   verified handle held across the launch denies write and delete.
///   </description></item>
///   <item><description>
///   <b>The bytes are never re-checked.</b> A downloader that reports success while
///   the file on disk no longer matches the sha256 it was verified under is exactly
///   the post-verification swap. Before the fix the runner launches it regardless;
///   after it, the launch is refused.
///   </description></item>
/// </list>
/// <para>
/// These are the negative tests for Task S3.1 step 6 and are written to compile and
/// <b>fail</b> on the parent commit — they use only the runners' pre-existing seams
/// and name no new type, so they can be dropped onto it as-is.
/// </para>
/// </remarks>
public sealed class StagedExecutionTests
{
    private const string ManifestUrl = "https://updates.example.com/acme/stable.json";

    private static string Sha256Hex(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// <summary>
    /// True when <paramref name="action"/> was refused by the OS because someone holds
    /// the file open with a sharing mode that denies the operation. Both
    /// <see cref="IOException"/> (sharing violation) and
    /// <see cref="UnauthorizedAccessException"/> count — the distinction is a Win32
    /// mapping detail, not a security one.
    /// </summary>
    private static bool Refused(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    // ── 1. The prerequisite path (PrerequisiteRunner.cs:237) ──────────────────

    [WindowsFact("Windows file sharing semantics")]
    public async Task Prerequisite_installer_is_held_write_and_delete_denied_across_its_launch()
    {
        // A real (local, self-signed HTTPS) download so the production acquire path
        // runs verbatim: resolve → https + sha256 → stage → verify → launch.
        var installerBytes = Encoding.UTF8.GetBytes("the-genuine-prerequisite-installer-bytes");
        using var server = new TlsHttpServer((_, _) => (200, installerBytes, 0));
        using var trusted = TrustServer(server);
        using var tmp = new TempDir();

        var marker = Path.Combine(tmp.Path, "installed.txt"); // absent → detect false
        var ctx = new StepContext(new Dictionary<string, object?>(), payloadRoot: tmp.Path);
        var prereq = new InstallerPrerequisite(
            Name: "Test Redist",
            Detect: $"file_exists('{marker.Replace('\\', '/')}')",
            Source: server.Url("/redist.exe"),
            Sha256: Sha256Hex(installerBytes),
            Args: null, ExitCodesOk: null, ScopeRequired: null, TimeoutSeconds: null,
            // R11 landed the Authenticode gate in front of this launch. These bytes are
            // synthetic and therefore unsigned, and this test is about the handle, not the
            // signature — so it takes the documented opt-out rather than asserting a
            // success the gate is now right to refuse.
            AllowUnsigned: true);

        string? launchedPath = null;
        byte[]? launchedBytes = null;
        var overwriteRefused = false;
        var deleteRefused = false;

        // The attacker's moment: the runner has resolved the path it is about to
        // execute. Anything that can still change those bytes here is R12. Everything
        // is observed from INSIDE the launch, because that is the only point at which
        // the window is open — and because the fixed runner tears the staging
        // directory down as soon as the launch returns.
        PrerequisiteRunner.Launcher attackingLauncher = (exePath, _, _, _) =>
        {
            launchedPath = exePath;
            launchedBytes = File.ReadAllBytes(exePath); // readers stay admitted
            overwriteRefused = Refused(() => File.WriteAllBytes(exePath, Encoding.UTF8.GetBytes("swapped")));
            deleteRefused = Refused(() => File.Delete(exePath));
            File.WriteAllText(marker, "1"); // "installed" → the re-detect guard passes
            return Task.FromResult((0, (string?)null));
        };

        var outcome = await PrerequisiteRunner.RunAsync(
            new[] { prereq }, ctx, InstallScope.User, progress: null, attackingLauncher, CancellationToken.None);

        outcome.Success.Should().BeTrue("the prerequisite itself installs cleanly — this test is about the staging");
        launchedPath.Should().NotBeNull();
        overwriteRefused.Should().BeTrue(
            "the engine must hold a write-denying handle on the staged installer across Process.Start — " +
            "without it, any process running as the same user can swap the bytes after they were verified");
        deleteRefused.Should().BeTrue(
            "delete must be denied too: a swap can be a delete followed by a re-create at the same path");
        launchedBytes.Should().Equal(
            installerBytes, "the bytes that reached the launch must be the bytes that were verified");
        File.Exists(launchedPath!).Should().BeFalse(
            "the private staging directory is torn down once the prerequisite has run");
    }

    [WindowsFact("Windows file sharing semantics")]
    public async Task Prerequisite_installer_is_not_staged_loose_in_the_temp_root()
    {
        // A per-run private directory, not a GUID-named file dropped straight into a
        // world-visible %TEMP%: the directory is what an attacker has to be able to
        // write into to win the race at all.
        var installerBytes = Encoding.UTF8.GetBytes("staged-somewhere-private");
        using var server = new TlsHttpServer((_, _) => (200, installerBytes, 0));
        using var trusted = TrustServer(server);
        using var tmp = new TempDir();

        var marker = Path.Combine(tmp.Path, "installed.txt");
        var ctx = new StepContext(new Dictionary<string, object?>(), payloadRoot: tmp.Path);
        var prereq = new InstallerPrerequisite(
            Name: "Test Redist",
            Detect: $"file_exists('{marker.Replace('\\', '/')}')",
            Source: server.Url("/redist.exe"),
            Sha256: Sha256Hex(installerBytes),
            Args: null, ExitCodesOk: null, ScopeRequired: null, TimeoutSeconds: null,
            // See above: R11's gate would otherwise refuse these unsigned synthetic bytes,
            // and this test is about where the file is staged, not about its signature.
            AllowUnsigned: true);

        string? launchedPath = null;
        PrerequisiteRunner.Launcher launcher = (exePath, _, _, _) =>
        {
            launchedPath = exePath;
            File.WriteAllText(marker, "1");
            return Task.FromResult((0, (string?)null));
        };

        var outcome = await PrerequisiteRunner.RunAsync(
            new[] { prereq }, ctx, InstallScope.User, progress: null, launcher, CancellationToken.None);

        outcome.Success.Should().BeTrue();
        var stagedIn = Path.GetFullPath(Path.GetDirectoryName(launchedPath!)!);
        var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        stagedIn.TrimEnd(Path.DirectorySeparatorChar).Should().NotBe(
            tempRoot, "the installer must be staged in its own private directory, not loose in %TEMP%");
    }

    // ── 2. The update path (UpdateRunner.cs:179-181) ──────────────────────────

    [Fact]
    public async Task Update_refuses_to_launch_a_staged_setup_whose_bytes_changed_after_verification()
    {
        // The downloader reports success (its own sha256 check passed) but what is on
        // disk when the runner goes to launch is something else — the post-verification
        // swap R12 describes, with the race already won.
        var swapped = Encoding.UTF8.GetBytes("attacker-substituted-setup-payload");
        var declaredSha = Sha256Hex(Encoding.UTF8.GetBytes("the-genuine-setup-payload"));

        var (manifest, signature, key) = SignedManifest("2.0.0", declaredSha);
        var downloader = new WritingDownloader(swapped);
        var launcher = new RecordingLauncher(0);
        var log = new List<string>();
        var runner = new UpdateRunner(
            Fetcher(manifest, signature), downloader, launcher, () => Installed("1.0.0"), (m, _) => log.Add(m));

        var code = await runner.RunAsync(Request(key), CancellationToken.None);

        launcher.Called.Should().BeFalse(
            "a staged setup whose bytes no longer match the sha256 it was verified under must never be launched");
        code.Should().Be(InstallSession.UpdateCheckFailedExitCode);
    }

    [WindowsFact("Windows file sharing semantics")]
    public async Task Update_holds_the_staged_setup_write_and_delete_denied_across_the_child_launch()
    {
        var packageBytes = Encoding.UTF8.GetBytes("the-genuine-setup-payload");
        var (manifest, signature, key) = SignedManifest("2.0.0", Sha256Hex(packageBytes));
        var downloader = new WritingDownloader(packageBytes);

        var overwriteRefused = false;
        var deleteRefused = false;
        var launcher = new RecordingLauncher(0, onLaunch: exePath =>
        {
            overwriteRefused = Refused(() => File.WriteAllBytes(exePath, Encoding.UTF8.GetBytes("swapped")));
            deleteRefused = Refused(() => File.Delete(exePath));
        });

        var runner = new UpdateRunner(
            Fetcher(manifest, signature), downloader, launcher, () => Installed("1.0.0"), (_, _) => { });

        var code = await runner.RunAsync(Request(key), CancellationToken.None);

        code.Should().Be(0);
        launcher.Called.Should().BeTrue();
        overwriteRefused.Should().BeTrue(
            "the update runner must hold a write-denying handle on the staged Setup.exe across the child launch");
        deleteRefused.Should().BeTrue("delete must be denied too — a swap can be a delete plus a re-create");
    }

    // ── 3. A staging failure is a diagnosable failure, not a crash ────────────

    [Fact]
    public async Task Update_reports_an_unusable_staging_root_instead_of_throwing()
    {
        // A TempDirectory that is a FILE, not a directory: creating the staging
        // directory under it throws. Every other failure in RunAsync comes back as a
        // typed exit code plus a report line, and the console host has no general catch,
        // so a redirected or ACL-hostile temp location must not become a crash.
        using var tmp = new TempDir();
        var notADirectory = Path.Combine(tmp.Path, "temp-is-a-file");
        File.WriteAllText(notADirectory, "x");

        var packageBytes = Encoding.UTF8.GetBytes("never-downloaded");
        var (manifest, signature, key) = SignedManifest("2.0.0", Sha256Hex(packageBytes));
        var downloader = new WritingDownloader(packageBytes);
        var launcher = new RecordingLauncher(0);
        var log = new List<string>();
        var runner = new UpdateRunner(
            Fetcher(manifest, signature), downloader, launcher, () => Installed("1.0.0"), (m, _) => log.Add(m));

        var request = new UpdateRequest(
            ManifestUrl: ManifestUrl, SigningKey: key, Channel: "stable",
            Scope: InstallScope.Machine, AppId: "com.acme.Studio", TempDirectory: notADirectory);

        var code = await runner.RunAsync(request, CancellationToken.None);

        code.Should().Be(InstallSession.UpdateCheckFailedExitCode,
            "a staging failure is reported the same typed way every other failure in this method is");
        launcher.Called.Should().BeFalse();
        string.Join("\n", log).Should().Contain("could not create a private staging directory");
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static UpdateRequest Request(string signingKey) =>
        new(ManifestUrl: ManifestUrl, SigningKey: signingKey, Channel: "stable",
            Scope: InstallScope.Machine, AppId: "com.acme.Studio", TempDirectory: Path.GetTempPath());

    private static UpgradeState Installed(string version) =>
        new(Found: true, InstalledVersion: version,
            PriorInstallDir: @"C:\Acme", PriorUninstallExe: @"C:\Acme\uninstall.exe",
            FoundScope: InstallScope.Machine);

    private static (byte[] Manifest, byte[] Signature, string PublicKeyBase64) SignedManifest(
        string version, string sha256)
    {
        var json =
            "{\n" +
            "  \"schemaVersion\": 1,\n" +
            $"  \"version\": \"{version}\",\n" +
            $"  \"packageUrl\": \"https://updates.example.com/acme/{version}/Setup.exe\",\n" +
            $"  \"sha256\": \"{sha256}\"\n" +
            "}";
        var bytes = Encoding.UTF8.GetBytes(json);
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
        var signature = ecdsa.SignData(bytes, HashAlgorithmName.SHA256);
        return (bytes, Encoding.UTF8.GetBytes(Convert.ToBase64String(signature)), spki);
    }

    private static MappedFetcher Fetcher(byte[] manifest, byte[] signature) =>
        new(url => url.EndsWith(".sig", StringComparison.Ordinal)
            ? UpdateResourceResult.Ok(signature)
            : UpdateResourceResult.Ok(manifest));

    private sealed class MappedFetcher : IUpdateResourceFetcher
    {
        private readonly Func<string, UpdateResourceResult> _map;

        public MappedFetcher(Func<string, UpdateResourceResult> map) => _map = map;

        public Task<UpdateResourceResult> FetchAsync(string url, CancellationToken ct)
            => Task.FromResult(_map(url));
    }

    /// <summary>
    /// A downloader that actually puts bytes on disk at the destination and reports
    /// success — so the runner faces the same file a real download would leave. Give
    /// it bytes that do NOT hash to the manifest's sha256 and it becomes the attacker
    /// who swapped the file the moment after it verified.
    /// </summary>
    private sealed class WritingDownloader : IUpdatePackageDownloader
    {
        private readonly byte[] _bytes;

        public WritingDownloader(byte[] bytes) => _bytes = bytes;

        public string? Destination { get; private set; }

        public Task<UpdatePackageDownloadResult> DownloadAsync(
            string url, string destination, string sha256, CancellationToken ct)
        {
            Destination = destination;
            File.WriteAllBytes(destination, _bytes);
            return Task.FromResult(UpdatePackageDownloadResult.Ok());
        }
    }

    private sealed class RecordingLauncher : IChildInstallerLauncher
    {
        private readonly int _exitCode;
        private readonly Action<string>? _onLaunch;

        public RecordingLauncher(int exitCode, Action<string>? onLaunch = null)
        {
            _exitCode = exitCode;
            _onLaunch = onLaunch;
        }

        public bool Called { get; private set; }

        public Task<int> RunAsync(string exePath, IReadOnlyList<string> args, CancellationToken ct)
        {
            Called = true;
            _onLaunch?.Invoke(exePath);
            return Task.FromResult(_exitCode);
        }
    }

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
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        return SigilHttpClient.UseForTesting(client);
    }

    /// <summary>
    /// Minimal HTTPS/1.1 server over <see cref="SslStream"/> with a self-signed
    /// certificate — the same shape as <c>HttpDownloadIntegrationTests</c>' server,
    /// duplicated here on purpose so this whole file can be dropped onto the parent
    /// commit unchanged to watch it fail.
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
