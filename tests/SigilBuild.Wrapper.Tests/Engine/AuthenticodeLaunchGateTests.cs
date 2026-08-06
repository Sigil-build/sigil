namespace SigilBuild.Wrapper.Tests.Engine;

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Tests.Helpers;
using Xunit;

/// <summary>
/// Register row R11, through the production prerequisite path: a binary this run
/// downloaded is Authenticode-checked immediately before it is launched, and an
/// unsigned one is refused.
/// </summary>
/// <remarks>
/// <para>
/// This file names no type and no member the fix introduced, so it can be dropped onto
/// the parent commit unchanged to watch it fail — on the parent the download's SHA-256
/// is the only gate there is, so the launcher is called and the prerequisite reports
/// success. The opt-out and the pure policy table live in
/// <c>DownloadedBinaryTrustTests</c>, which necessarily does name new members.
/// </para>
/// <para>
/// <b>Elevated vs unelevated — what each test asserts on each kind of host.</b>
/// <c>WinVerifyTrust</c>'s answer for a file with no signature is
/// <c>TRUST_E_NOSIGNATURE</c> / <c>TRUST_E_SUBJECT_FORM_UNKNOWN</c> whether or not the
/// caller holds an administrator token — the verdict is a property of the file, not of
/// the process. So on BOTH host kinds these tests take the same branch and assert the
/// same thing: refusal, launcher never called. Nothing here reads an elevation token,
/// and no assertion is guarded by one, so neither branch can be vacuous. The one thing
/// elevation does change is <em>where</em> the file is staged, and the test assembly's
/// process-wide floor (<c>SecureStaging.NeverStageElevatedForTesting</c>) pins that off
/// the real <c>%ProgramData%</c> on an elevated runner — CI is elevated.
/// </para>
/// <para>
/// <b>Host damage.</b> Nothing is written outside a <see cref="TempDir"/>, no
/// certificate is added to any store, no real <c>%ProgramData%</c> path is touched, and
/// no launcher here starts a process — the seam records the call instead.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class AuthenticodeLaunchGateTests
{
    private static string Sha256Hex(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// <summary>
    /// The decisive R11 case. The synthetic body is deterministically unsigned — it is
    /// not even a PE image — so no host, network or certificate store can make this
    /// answer differently.
    /// </summary>
    [WindowsFact("Authenticode / WinVerifyTrust")]
    public async Task An_unsigned_downloaded_prerequisite_is_never_launched()
    {
        var installerBytes = Encoding.UTF8.GetBytes("an-unsigned-prerequisite-installer");
        using var server = new TlsHttpServer(installerBytes);
        using var trusted = TrustServer(server);
        using var tmp = new TempDir();

        var marker = Path.Combine(tmp.Path, "installed.txt"); // absent → detect false
        var ctx = new StepContext(new Dictionary<string, object?>(), payloadRoot: tmp.Path);
        var prereq = new InstallerPrerequisite(
            Name: "Unsigned Redist",
            Detect: $"file_exists('{marker.Replace('\\', '/')}')",
            Source: server.Url("/redist.exe"),
            Sha256: Sha256Hex(installerBytes),
            Args: null, ExitCodesOk: null, ScopeRequired: null, TimeoutSeconds: null);

        var launched = false;
        PrerequisiteRunner.Launcher launcher = (_, _, _, _) =>
        {
            launched = true;
            File.WriteAllText(marker, "1");
            return Task.FromResult((0, (string?)null));
        };

        var outcome = await PrerequisiteRunner.RunAsync(
            new[] { prereq }, ctx, InstallScope.User, progress: null, launcher, CancellationToken.None);

        launched.Should().BeFalse(
            "a downloaded binary must be Authenticode-checked immediately before it is launched — the " +
            "manifest's sha256 only says 'these are the bytes the manifest names', which is worth nothing " +
            "against an origin that serves different bytes and the matching digest");
        outcome.Success.Should().BeFalse();
        outcome.Error.Should().Contain("refusing to run");
        File.Exists(marker).Should().BeFalse("the refusal happens before the installer runs, not after");
    }

    /// <summary>
    /// The positive control that keeps the test above honest: the same runner, the same
    /// download, the same launcher — with a <c>payload://</c> source instead of an
    /// <c>https://</c> one — still launches. A bundled binary is covered by the package's
    /// own signature and is deliberately NOT gated, so "refused" above cannot be read as
    /// "the prerequisite runner stopped working".
    /// </summary>
    [WindowsFact("Authenticode / WinVerifyTrust")]
    public async Task A_bundled_prerequisite_is_not_gated_and_still_launches()
    {
        using var tmp = new TempDir();
        var payloadRoot = Path.Combine(tmp.Path, "payload");
        Directory.CreateDirectory(payloadRoot);
        // Equally unsigned, equally not a PE image — the only difference is where it
        // came from, which is exactly the distinction being asserted.
        await File.WriteAllBytesAsync(
            Path.Combine(payloadRoot, "redist.exe"), Encoding.UTF8.GetBytes("an-unsigned-bundled-installer"));

        var marker = Path.Combine(tmp.Path, "installed.txt");
        var ctx = new StepContext(new Dictionary<string, object?>(), payloadRoot: payloadRoot);
        var prereq = new InstallerPrerequisite(
            Name: "Bundled Redist",
            Detect: $"file_exists('{marker.Replace('\\', '/')}')",
            Source: "payload://redist.exe",
            Sha256: null,
            Args: null, ExitCodesOk: null, ScopeRequired: null, TimeoutSeconds: null);

        var launched = false;
        PrerequisiteRunner.Launcher launcher = (_, _, _, _) =>
        {
            launched = true;
            File.WriteAllText(marker, "1");
            return Task.FromResult((0, (string?)null));
        };

        var outcome = await PrerequisiteRunner.RunAsync(
            new[] { prereq }, ctx, InstallScope.User, progress: null, launcher, CancellationToken.None);

        launched.Should().BeTrue(
            "a payload:// prerequisite's integrity comes from the artifact's own signature, so gating it " +
            "again would demand a second signature on every bundled helper for nothing");
        outcome.Success.Should().BeTrue();
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

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
    /// Minimal HTTPS/1.1 origin serving one fixed body — the same shape as the servers in
    /// <c>HttpDownloadIntegrationTests</c> and <c>StagedExecutionTests</c>, duplicated
    /// here for the same reason they duplicate it: so this file stands alone on the
    /// parent commit.
    /// </summary>
    private sealed class TlsHttpServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly X509Certificate2 _cert;
        private readonly byte[] _body;

        public TlsHttpServer(byte[] body)
        {
            _body = body;
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
#pragma warning disable CA1031 // test origin: swallow all per-connection errors
            try
            {
                using (client)
                await using (var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false))
                {
                    await ssl.AuthenticateAsServerAsync(_cert, clientCertificateRequired: false,
                        checkCertificateRevocation: false).ConfigureAwait(false);

                    _ = await ReadLineAsync(ssl).ConfigureAwait(false);
                    while (!string.IsNullOrEmpty(await ReadLineAsync(ssl).ConfigureAwait(false))) { }

                    var header = Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 200 OK\r\nContent-Length: {_body.Length}\r\nConnection: close\r\n\r\n");
                    await ssl.WriteAsync(header).ConfigureAwait(false);
                    await ssl.WriteAsync(_body).ConfigureAwait(false);
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
            // Ephemeral and never installed into any certificate store — a test must not
            // mutate the host's trust configuration.
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
