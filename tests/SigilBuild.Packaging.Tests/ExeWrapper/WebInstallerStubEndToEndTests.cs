using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Win32;
using SigilBuild.Core.Manifest;
using SigilBuild.Packaging.ExeWrapper;
using SigilBuild.Wrapper.Cli;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Json;
using Xunit;

namespace SigilBuild.Packaging.Tests.ExeWrapper;

/// <summary>
/// P12 / T12.6: the web-installer stub (T12.5's
/// <see cref="ExeWrapperPackager.BuildWebStubBlobBytes"/>) exercised end-to-end —
/// its REAL two-step blob (<c>http_download</c> + <c>run_program</c>) is run
/// through <c>InstallSession</c> against a REAL local HTTPS server (mirroring
/// <c>HttpDownloadIntegrationTests</c>): the stub downloads the served "package",
/// verifies its actual sha256, and chains into running it. A tampered served
/// package must fail the stub's own checksum check (rollback), never reaching
/// run_program. Also re-proves T12.5's CRITICAL delegation fix
/// (<see cref="WrapperBlob.IsDelegatingStub"/>) end-to-end through this REAL
/// download+run pair, not the hand-rolled marker step
/// <c>WebInstallerStubCompletionTests</c> uses for its narrower unit check.
/// </summary>
/// <remarks>
/// <para>
/// <b>What's real, what's a stand-in:</b> the HTTP fetch, the checksum
/// verification, and the "child" process launch are all REAL (a genuine local
/// HTTPS download followed by a genuine <c>Process.Start</c>). The "package" the
/// stub downloads and runs is a copy of <c>cmd.exe</c> invoked with a single
/// <c>/verysilent</c> argument (verified locally to exit 0 immediately with
/// stdout/stderr redirected and no stdin redirect — exactly
/// <c>RunProgramStep</c>'s process configuration) — a real, trivial child that
/// proves the stub's hand-off actually executes an external process, standing in
/// for a full nested Setup.exe (which does not itself perform its own
/// ARP/completion bookkeeping here). The assertions below are entirely about the
/// STUB's OWN behavior — exactly the seam T12.5's fix gates — not about whatever
/// the downloaded child does. A true nested "real Setup.exe downloads and installs
/// a second real Setup.exe" cross-process scenario is CI-VM-only; see the P12 job
/// appended to <c>.github/workflows/wrapper-vm-tests.yml</c>.
/// </para>
/// <para>Windows-only (real HKCU registry + file system); a no-op elsewhere.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WebInstallerStubEndToEndTests
{
    private const string UninstallRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string CmdExePath = @"C:\Windows\System32\cmd.exe";

    private static SigilManifest Manifest(string appId) => new(
        "v1.0",
        new AppSection(appId, "Acme Web Stub E2E", "3.2.0", "Acme, Inc.", null, null),
        new BuildSection("./out", null, null, true),
        null, null, null, null,
        Installer: null,
        Location: SourceLocation.Unknown);

    private static byte[] ReadCmdExeBytes()
    {
        File.Exists(CmdExePath).Should().BeTrue("this test relies on a real, trivial Windows executable (cmd.exe) as a stand-in package");
        return File.ReadAllBytes(CmdExePath);
    }

    private static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static WrapperBlob BuildStubBlob(string appId, string packageUrl, string packageSha256, string fullPackageFileName)
    {
        var bytes = ExeWrapperPackager.BuildWebStubBlobBytes(Manifest(appId), packageUrl, packageSha256, fullPackageFileName);
        var serializable = JsonSerializer.Deserialize(
            Encoding.UTF8.GetString(bytes), WrapperBlobJsonContext.Default.SerializableWrapperBlob)!;
        return SerializableWrapperBlob.ToWrapperBlob(serializable);
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

    private static async Task<InstallOutcome> InstallOnceAsync(WrapperBlob blob)
    {
        var parsed = CommandLineParser.Parse(new[] { "/silent" }, blob.Parameters);
        var session = InstallSession.ForTesting(blob, parsed);
        return await session.RunInstallCoreAsync(Array.Empty<byte>(), progress: null);
    }

    [Fact]
    public async Task Stub_downloads_the_real_package_verifies_its_sha256_and_runs_it()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var appId = "sigil.webstub.e2e." + Guid.NewGuid().ToString("N");
        var packageBytes = ReadCmdExeBytes();
        var packageSha256 = Sha256Hex(packageBytes);
        // Unique per test run so two runs can never collide over a shared name.
        var fullPackageFileName = $"Acme-3.2.0-x64-Setup-{Guid.NewGuid():N}.exe";

        using var server = new RoutingTlsServer();
        using var _ = TrustServer(server);
        server.Map("/pkg/" + fullPackageFileName, packageBytes);

        // Pin where {staging_dir} resolves. InstallSession builds its own StepContext and
        // offers no seam of its own, so without this the stub would stage into the REAL
        // %ProgramData% on an elevated host — which CI is — and execute a copy of cmd.exe
        // out of it.
        using var scratch = new ScratchDir();
        using var siting = SecureStaging.UseSitingForTesting(scratch.Path);

        // Register row R5's "pre-planted" half, end to end. This is the path the stub used
        // to download to — {temp_dir}/<App>-<ver>-<arch>-Setup.exe, a pack-time constant
        // derived from the public artifact name and therefore known to anyone holding the
        // installer. Pre-fix the download landed exactly here, and HttpDownloadStep would
        // have backed this file up and overwritten it. It must now be inert.
        var oldPredictableDest = Path.Combine(Path.GetTempPath(), fullPackageFileName);
        var prePlanted = Encoding.UTF8.GetBytes("attacker bytes waiting at the predictable path");
        await File.WriteAllBytesAsync(oldPredictableDest, prePlanted);

        var blob = BuildStubBlob(appId, server.Url("/pkg/" + fullPackageFileName), packageSha256, fullPackageFileName);
        var installDir = Path.Combine(ScopeLayout.For(InstallScope.User).InstallRoot, appId);
        var tempDest = oldPredictableDest;

        try
        {
            var outcome = await InstallOnceAsync(blob);

            outcome.Success.Should().BeTrue("the served package's sha256 matches, so the download verifies and the run succeeds");
            server.Hits("/pkg/" + fullPackageFileName).Should().Be(
                1, "the stub's http_download step must fetch the package exactly once over real HTTP");

            // The download went to the per-run staging directory, not the predictable one.
            File.ReadAllBytes(oldPredictableDest).Should().Equal(
                prePlanted,
                "a file pre-planted at the OLD {temp_dir} destination must be neither overwritten nor " +
                "backed-up-and-restored: the stub no longer downloads to any path derivable from the " +
                "artifact name, which is half of register row R5");

            // …and the engine released the staging directory when the run ended, so the
            // downloaded package is not left behind. (R5's other half — re-verifying the
            // staged file under a held handle immediately before run_program — is asserted
            // decisively in HttpDownloadIntegrationTests, which can insert a tampering step
            // between the two; the stub's own two-step blob has no room for one.)
            Directory.EnumerateFileSystemEntries(scratch.Path).Should().BeEmpty(
                "the run staged inside the pinned root and cleaned it up afterwards");

            // Delegation check (T12.5's critical fix), proven through the REAL
            // http_download + run_program pair rather than a hand-rolled marker step.
            using (var arp = Registry.CurrentUser.OpenSubKey($@"{UninstallRoot}\{appId}"))
            {
                arp.Should().BeNull("a delegating web-installer stub must never write its OWN Add/Remove Programs row");
            }
            File.Exists(Path.Combine(installDir, "uninstall.exe")).Should().BeFalse(
                "a delegating stub must never copy itself in as uninstall.exe");
            UninstallStateStore.TryLoad(appId, InstallScope.User).Should().BeNull(
                "a delegating stub must never persist its own uninstall.json journal");
        }
        finally
        {
            Cleanup(appId, installDir);
            TryDeleteFile(tempDest);
        }
    }

    [Fact]
    public async Task Tampered_served_package_fails_the_stubs_checksum_check_and_never_runs()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var appId = "sigil.webstub.e2e.tampered." + Guid.NewGuid().ToString("N");
        var intendedBytes = ReadCmdExeBytes();
        var intendedSha256 = Sha256Hex(intendedBytes);
        // The server serves DIFFERENT bytes than what the stub's blob expects —
        // simulating a corrupted/tampered download.
        var servedBytes = Encoding.UTF8.GetBytes("not-the-real-package-just-tampered-substitute-bytes");
        // Unique per test run — see the happy-path test's comment on fullPackageFileName.
        var fullPackageFileName = $"Acme-3.2.0-x64-Setup-{Guid.NewGuid():N}.exe";

        using var server = new RoutingTlsServer();
        using var _ = TrustServer(server);
        server.Map("/pkg/" + fullPackageFileName, servedBytes);

        // See the happy-path test: pinning the siting keeps this off the real %ProgramData%
        // on an elevated runner, and gives the rollback assertion somewhere to look.
        using var scratch = new ScratchDir();
        using var siting = SecureStaging.UseSitingForTesting(scratch.Path);

        var blob = BuildStubBlob(appId, server.Url("/pkg/" + fullPackageFileName), intendedSha256, fullPackageFileName);
        var installDir = Path.Combine(ScopeLayout.For(InstallScope.User).InstallRoot, appId);
        var tempDest = Path.Combine(Path.GetTempPath(), fullPackageFileName);

        try
        {
            var outcome = await InstallOnceAsync(blob);

            outcome.Success.Should().BeFalse("a sha256 mismatch on the downloaded package must fail the stub's http_download step");
            Directory.EnumerateFiles(scratch.Path, fullPackageFileName, SearchOption.AllDirectories)
                .Should().BeEmpty(
                    "a checksum-mismatch download must roll back (delete) the partial file — asserted where " +
                    "the download now actually lands, the per-run staging directory, not the %TEMP% path the " +
                    "stub stopped using");
            File.Exists(tempDest).Should().BeFalse(
                "and nothing is written to the old predictable destination either");

            // No completion bookkeeping on a failed install either way (control: the
            // gate never even gets consulted on a step-failure outcome).
            using (var arp = Registry.CurrentUser.OpenSubKey($@"{UninstallRoot}\{appId}"))
            {
                arp.Should().BeNull("a failed stub install must never register an ARP row");
            }
        }
        finally
        {
            Cleanup(appId, installDir);
            TryDeleteFile(tempDest);
        }
    }

    /// <summary>
    /// A throwaway directory that stands in for <c>%ProgramData%</c> while a test runs, so
    /// nothing here ever stages into — or executes out of — a real machine-wide path.
    /// </summary>
    private sealed class ScratchDir : IDisposable
    {
        public ScratchDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"sigil-webstub-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
#pragma warning disable CA1031 // Best-effort test cleanup.
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
                // A leftover scratch directory is the OS temp sweeper's problem.
            }
#pragma warning restore CA1031
        }
    }

    private static void Cleanup(string appId, string installDir)
    {
#pragma warning disable CA1031 // Best-effort test cleanup.
        try { ArpRegistration.Remove(appId, InstallScope.User); } catch { }
        try { UninstallStateStore.Delete(appId, InstallScope.User); } catch { }
        try { if (Directory.Exists(installDir)) Directory.Delete(installDir, recursive: true); } catch { }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Best-effort delete of the stub's downloaded temp package. The production
    /// blob (<see cref="ExeWrapperPackager.BuildWebStubBlobBytes"/>) defines no
    /// cleanup step for this file — a pre-existing, out-of-scope-for-T12.6 gap
    /// noted in the task report — so tests clean up after themselves instead of
    /// leaving copies of cmd.exe (or tampered substitute bytes) behind in %TEMP%.
    /// </summary>
    private static void TryDeleteFile(string path)
    {
#pragma warning disable CA1031 // Best-effort test cleanup.
        try { if (File.Exists(path)) File.Delete(path); } catch { }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Minimal HTTPS/1.1 server over <see cref="SslStream"/> with a self-signed
    /// certificate, routing by exact request path to a mapped byte payload (200)
    /// or 404 for anything unmapped. Mirrors
    /// <c>HttpDownloadIntegrationTests.TlsHttpServer</c> (P4) generalized with a
    /// per-path hit counter, as <c>UpdateEndToEndTests.RoutingTlsServer</c> does.
    /// </summary>
    private sealed class RoutingTlsServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly X509Certificate2 _cert;
        private readonly System.Collections.Generic.Dictionary<string, byte[]> _routes = new(StringComparer.Ordinal);
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _hits = new(StringComparer.Ordinal);

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
