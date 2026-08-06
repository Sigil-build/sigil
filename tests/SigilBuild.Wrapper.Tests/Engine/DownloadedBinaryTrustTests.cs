namespace SigilBuild.Wrapper.Tests.Engine;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
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
/// Register rows R11 (Authenticode before every elevated launch) and R17 (revocation
/// checking) — the policy table, the HRESULT classification, the three-state trust
/// line, and the two launch sites that need the internal test seam.
/// </summary>
/// <remarks>
/// <para>
/// <b>What could not be tested, and why it is here anyway.</b> R17's decisive
/// behaviours — a revoked certificate produces no trust line, an unreachable CRL
/// distribution point produces the distinct third state — need a revoked-certificate
/// fixture and an offline-CRL fixture. Neither is constructible on the development box
/// (Authenticode-signing a PE needs signtool and a certificate this session cannot
/// install, and this session is not elevated). The classification is therefore split
/// out of the P/Invoke into <see cref="AuthenticodeVerifier.Classify"/> and its whole
/// table asserted directly from the documented HRESULTs. That is a weaker proof than a
/// real revoked binary and is stated as such: it proves the mapping, not the API's
/// return value.
/// </para>
/// <para>
/// <b>Elevated vs unelevated.</b> Every test in this file asserts the identical thing
/// on both. The policy and classification tests are pure functions of an enum and an
/// integer — no token, no ACL, no file. The two launch-site tests turn on
/// <c>WinVerifyTrust</c>'s verdict for a file with no signature, which is a property of
/// the file and not of the caller's token; they are <c>[WindowsFact]</c> because off
/// Windows the verdict is <see cref="AuthenticodeStatus.NotEvaluated"/> and the
/// assertion would be vacuous. No assertion anywhere here is guarded by an elevation
/// check, so no branch can be vacuous on either host kind.
/// </para>
/// <para>
/// <b>Host damage.</b> No certificate is installed into any store, nothing is written
/// outside a <see cref="TempDir"/>, no real <c>%ProgramData%</c> path is touched, and
/// no process is started — the two launch-site tests are constructed so that the
/// non-refusing branch fails at <c>Process.Start</c> on a non-PE file rather than
/// executing anything.
/// </para>
/// </remarks>
public sealed class DownloadedBinaryTrustTests
{
    private const string What = "prerequisite 'Acme Redist'";

    // ── 1. R17: the HRESULT classification ────────────────────────────────────

    [Theory]
    [InlineData(0, AuthenticodeStatus.Trusted)]
    [InlineData(unchecked((int)0x800B010C), AuthenticodeStatus.Revoked)]              // CERT_E_REVOKED
    [InlineData(unchecked((int)0x800B0111), AuthenticodeStatus.Revoked)]              // TRUST_E_EXPLICIT_DISTRUST
    [InlineData(unchecked((int)0x80092013), AuthenticodeStatus.RevocationUnavailable)] // CRYPT_E_REVOCATION_OFFLINE
    [InlineData(unchecked((int)0x80092012), AuthenticodeStatus.RevocationUnavailable)] // CRYPT_E_NO_REVOCATION_CHECK
    [InlineData(unchecked((int)0x800B010E), AuthenticodeStatus.RevocationUnavailable)] // CERT_E_REVOCATION_FAILURE
    [InlineData(unchecked((int)0x800B0100), AuthenticodeStatus.NoSignature)]          // TRUST_E_NOSIGNATURE
    [InlineData(unchecked((int)0x800B0003), AuthenticodeStatus.NoSignature)]          // TRUST_E_SUBJECT_FORM_UNKNOWN
    [InlineData(unchecked((int)0x800B0001), AuthenticodeStatus.NoSignature)]          // TRUST_E_PROVIDER_UNKNOWN
    [InlineData(unchecked((int)0x80096010), AuthenticodeStatus.Invalid)]              // TRUST_E_BAD_DIGEST
    [InlineData(unchecked((int)0x800B0109), AuthenticodeStatus.Invalid)]              // CERT_E_UNTRUSTEDROOT
    [InlineData(unchecked((int)0x800B0101), AuthenticodeStatus.Invalid)]              // CERT_E_EXPIRED
    [InlineData(unchecked((int)0x8000FFFF), AuthenticodeStatus.Invalid)]              // E_UNEXPECTED — the default bucket
    public void Classify_separates_revoked_from_unreachable_from_unsigned(int hresult, AuthenticodeStatus expected)
    {
        AuthenticodeVerifier.Classify(hresult).Should().Be(expected);
    }

    /// <summary>
    /// The heart of R17: "offline" is neither "trusted" nor "forged". Asserted as a
    /// three-way separation rather than as three independent mappings, because the bug
    /// being closed was precisely a collapse of the three into two.
    /// </summary>
    [Fact]
    public void Revoked_offline_and_valid_are_three_distinct_verdicts()
    {
        var valid = AuthenticodeVerifier.Classify(0);
        var offline = AuthenticodeVerifier.Classify(unchecked((int)0x80092013));
        var revoked = AuthenticodeVerifier.Classify(unchecked((int)0x800B010C));

        new[] { valid, offline, revoked }.Should().OnlyHaveUniqueItems(
            "an unreachable revocation responder must not be reported as trust, nor as forgery — " +
            "the first makes an offline box read a revoked publisher as good, the second tells every " +
            "offline user their genuine installer looks forged");
    }

    // ── 2. R17: the trust line the wizard renders ─────────────────────────────

    [Fact]
    public void The_trust_line_renders_a_distinct_state_when_revocation_is_unavailable()
    {
        var trusted = InstallerTrustLoader.ResolveTrustLine(true, AuthenticodeStatus.Trusted, "Acme Corp");
        var offline = InstallerTrustLoader.ResolveTrustLine(true, AuthenticodeStatus.RevocationUnavailable, "Acme Corp");

        trusted.Should().Be("Signed by Acme Corp");
        offline.Should().NotBeNull("silence would tell an offline user their genuine installer looks forged");
        offline.Should().NotBe(trusted, "and showing the plain line would claim 'still valid' on no evidence");
        offline.Should().Contain("revocation status unavailable");
    }

    [Theory]
    [InlineData(AuthenticodeStatus.Revoked)]
    [InlineData(AuthenticodeStatus.NoSignature)]
    [InlineData(AuthenticodeStatus.Invalid)]
    [InlineData(AuthenticodeStatus.NotEvaluated)]
    public void A_revoked_or_untrustworthy_signature_renders_no_trust_line(AuthenticodeStatus status)
    {
        InstallerTrustLoader.ResolveTrustLine(true, status, "Acme Corp").Should().BeNull();
    }

    [Fact]
    public void An_artifact_that_never_declared_signing_still_renders_no_line()
    {
        InstallerTrustLoader.ResolveTrustLine(false, AuthenticodeStatus.Trusted, "Acme Corp").Should().BeNull();
    }

    // ── 3. R11: the launch policy ─────────────────────────────────────────────

    [Fact]
    public void An_unsigned_download_is_refused_by_default()
    {
        var (refusal, _, _) = DownloadedBinaryTrust.Decide(AuthenticodeStatus.NoSignature, What, allowUnsigned: false);

        refusal.Should().NotBeNull();
        refusal.Should().Contain("no Authenticode signature");
        refusal.Should().Contain("allow_unsigned", "the message must name the way out, or it is not actionable");
    }

    [Fact]
    public void An_unsigned_download_declared_allow_unsigned_is_permitted_and_says_so()
    {
        var (refusal, report, isError) =
            DownloadedBinaryTrust.Decide(AuthenticodeStatus.NoSignature, What, allowUnsigned: true);

        refusal.Should().BeNull("unsigned redistributables are common and legitimate — that is why the opt-out exists");
        report.Should().Contain("allow_unsigned");
        isError.Should().BeTrue("taking the opt-out is worth a line the operator can see in the log");
    }

    /// <summary>
    /// The rule that makes the opt-out defensible: it waives an ABSENCE of evidence, not
    /// a positive statement that the key is bad. Without this, <c>allow_unsigned</c> would
    /// be a manifest field that turns off revocation, which is not a trade any author
    /// should be able to make by accident.
    /// </summary>
    [Fact]
    public void A_revoked_certificate_is_refused_even_with_allow_unsigned()
    {
        var (waived, _, _) = DownloadedBinaryTrust.Decide(AuthenticodeStatus.Revoked, What, allowUnsigned: true);
        var strict = DownloadedBinaryTrust.Decide(AuthenticodeStatus.Revoked, What, allowUnsigned: false).Refusal;

        waived.Should().NotBeNull("no manifest flag overrides a certificate authority saying the key is bad");
        waived.Should().Contain("REVOKED");
        strict.Should().NotBeNull();
    }

    [Fact]
    public void A_signature_that_does_not_establish_trust_is_refused_by_default_and_waivable()
    {
        DownloadedBinaryTrust.Decide(AuthenticodeStatus.Invalid, What, allowUnsigned: false)
            .Refusal.Should().NotBeNull();
        DownloadedBinaryTrust.Decide(AuthenticodeStatus.Invalid, What, allowUnsigned: true)
            .Refusal.Should().BeNull(
                "a broken or untrusted signature is no better and no worse than none at all, so the same " +
                "opt-out covers it — the sha256 is enforced either way");
    }

    /// <summary>
    /// Refusing an unreachable CRL responder would stop every air-gapped or
    /// egress-restricted install dead, which is a far likelier outcome than the attack it
    /// would prevent. It proceeds — loudly.
    /// </summary>
    [Fact]
    public void An_unreachable_revocation_responder_proceeds_but_never_silently()
    {
        var (refusal, report, isError) =
            DownloadedBinaryTrust.Decide(AuthenticodeStatus.RevocationUnavailable, What, allowUnsigned: false);

        refusal.Should().BeNull();
        isError.Should().BeTrue();
        report.Should().Contain("could NOT be established");
        report.Should().Contain("not a confirmation",
            "the line must not read as an endorsement — that is the difference between this state and Trusted");
    }

    [Fact]
    public void A_trusted_signature_is_permitted_and_a_verdict_never_sought_is_not_a_failure()
    {
        DownloadedBinaryTrust.Decide(AuthenticodeStatus.Trusted, What, allowUnsigned: false)
            .Refusal.Should().BeNull();

        var (refusal, report, _) =
            DownloadedBinaryTrust.Decide(AuthenticodeStatus.NotEvaluated, What, allowUnsigned: false);
        refusal.Should().BeNull("off Windows nothing was examined, so nothing was found wanting");
        report.Should().BeNull();
    }

    // ── 4. R11: the prerequisite opt-out, through the production runner ────────

    [WindowsFact("Authenticode / WinVerifyTrust")]
    public async Task A_prerequisite_declaring_allow_unsigned_is_launched()
    {
        using var tmp = new TempDir();
        var payloadRoot = Path.Combine(tmp.Path, "payload");
        Directory.CreateDirectory(payloadRoot);
        var marker = Path.Combine(tmp.Path, "installed.txt");

        // Downloading needs an origin; the opt-out itself is what is under test, so the
        // source is bundled and the assertion is that the flag parses and reaches the
        // engine unchanged. The gated-download case is AuthenticodeLaunchGateTests'.
        await File.WriteAllBytesAsync(
            Path.Combine(payloadRoot, "redist.exe"), Encoding.UTF8.GetBytes("unsigned"));

        var ctx = new StepContext(new Dictionary<string, object?>(), payloadRoot: payloadRoot);
        var prereq = new InstallerPrerequisite(
            Name: "Acme Redist",
            Detect: $"file_exists('{marker.Replace('\\', '/')}')",
            Source: "payload://redist.exe",
            AllowUnsigned: true);

        var launched = false;
        PrerequisiteRunner.Launcher launcher = (_, _, _, _) =>
        {
            launched = true;
            File.WriteAllText(marker, "1");
            return Task.FromResult((0, (string?)null));
        };

        var outcome = await PrerequisiteRunner.RunAsync(
            new[] { prereq }, ctx, InstallScope.User, progress: null, launcher, CancellationToken.None);

        outcome.Success.Should().BeTrue();
        launched.Should().BeTrue();
        prereq.AllowUnsigned.Should().BeTrue("the flag must survive the manifest graph unchanged");
    }

    // ── 5. R11: the web-stub payload (run_program of a downloaded file) ────────

    /// <summary>
    /// The web-installer stub is an <c>http_download</c> followed by a
    /// <c>run_program</c> of the same path, running <c>requireAdministrator</c>. The
    /// staging work in this lane made those two steps share a verified handle; this makes
    /// the signature a condition of the launch as well.
    /// </summary>
    /// <remarks>
    /// The program is a non-PE scratch file, so the two branches are told apart without
    /// executing anything: refused → "was not started … refusing to run"; permitted →
    /// <c>Process.Start</c> itself fails with "failed to start". A test that had to
    /// launch something to prove the gate was off would be a test that launches something.
    /// </remarks>
    [WindowsFact("Authenticode / WinVerifyTrust")]
    public async Task A_downloaded_program_is_signature_checked_before_run_program_launches_it()
    {
        using var tmp = new TempDir();
        var program = Path.Combine(tmp.Path, "downloaded.exe");
        var bytes = Encoding.UTF8.GetBytes("an-unsigned-downloaded-payload");
        await File.WriteAllBytesAsync(program, bytes);
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        var step = new InstallStep.RunProgram(
            "launch", program, Args: null, Wait: true, Cwd: null,
            ExpectedExitCodes: new[] { 0 }, TimeoutSeconds: 30, When: null, OnFailure.Fail);

        // Armed: this artifact declared signing, so what it downloads must be signed.
        using (DownloadedBinaryTrust.RequireForTesting(true))
        {
            var ctx = new StepContext(new Dictionary<string, object?>(StringComparer.Ordinal));
            ctx.RecordVerifiedDownload(program, sha);

            var result = await new InstallEngine().RunAsync(new InstallStep[] { step }, ctx);

            result.Success.Should().BeFalse();
            result.Error.Should().Contain("refusing to run",
                "a signed installer must not execute an unsigned binary it pulled off the network");
        }

        // Disarmed — the positive control. Same file, same step: the gate is no longer
        // the reason it does not run, and the failure that remains is the OS refusing a
        // non-PE image, not a trust refusal.
        using (DownloadedBinaryTrust.RequireForTesting(false))
        {
            var ctx = new StepContext(new Dictionary<string, object?>(StringComparer.Ordinal));
            ctx.RecordVerifiedDownload(program, sha);

            var result = await new InstallEngine().RunAsync(new InstallStep[] { step }, ctx);

            result.Error.Should().NotContain("refusing to run",
                "with the requirement off, the Authenticode gate must not be what stops this");
            result.Error.Should().Contain("failed to start");
        }
    }

    // ── 6. R11: the update package ────────────────────────────────────────────

    [WindowsFact("Authenticode / WinVerifyTrust")]
    public async Task An_unsigned_update_package_is_never_launched()
    {
        using var tmp = new TempDir();
        var packageBytes = Encoding.UTF8.GetBytes("an-unsigned-setup-payload");
        var (manifest, signature, key) = SignedManifest("2.0.0", Sha256Hex(packageBytes));
        var launcher = new RecordingLauncher();
        var log = new List<string>();

        using (DownloadedBinaryTrust.RequireForTesting(true))
        {
            var runner = new UpdateRunner(
                Fetcher(manifest, signature), new WritingDownloader(packageBytes), launcher,
                () => Installed("1.0.0"), (m, _) => log.Add(m));

            var code = await runner.RunAsync(Request(key, tmp.Path), CancellationToken.None);

            launcher.Called.Should().BeFalse(
                "the channel manifest's signature authenticates the sha256 and the sha256 authenticates the " +
                "bytes — but only against the manifest, and this process is about to run them elevated");
            code.Should().Be(InstallSession.UpdateManifestRejectedExitCode);
            string.Join("\n", log).Should().Contain("refusing to run");
        }
    }

    /// <summary>
    /// The positive control: the same unsigned package, with the requirement off, still
    /// installs. An installer that never claimed a signed provenance keeps its
    /// <c>/Update</c> path — without this, the gate above could equally mean "/Update no
    /// longer works".
    /// </summary>
    [Fact]
    public async Task An_artifact_that_did_not_declare_signing_still_updates()
    {
        using var tmp = new TempDir();
        var packageBytes = Encoding.UTF8.GetBytes("an-unsigned-setup-payload");
        var (manifest, signature, key) = SignedManifest("2.0.0", Sha256Hex(packageBytes));
        var launcher = new RecordingLauncher();

        using (DownloadedBinaryTrust.RequireForTesting(false))
        {
            var runner = new UpdateRunner(
                Fetcher(manifest, signature), new WritingDownloader(packageBytes), launcher,
                () => Installed("1.0.0"), (_, _) => { });

            var code = await runner.RunAsync(Request(key, tmp.Path), CancellationToken.None);

            code.Should().Be(0);
            launcher.Called.Should().BeTrue();
        }
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static string Sha256Hex(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static UpdateRequest Request(string signingKey, string tempDirectory) =>
        new(ManifestUrl: "https://updates.example.com/acme/stable.json", SigningKey: signingKey,
            Channel: "stable", Scope: InstallScope.Machine, AppId: "com.acme.Studio",
            TempDirectory: tempDirectory);

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

    private sealed class WritingDownloader : IUpdatePackageDownloader
    {
        private readonly byte[] _bytes;

        public WritingDownloader(byte[] bytes) => _bytes = bytes;

        public Task<UpdatePackageDownloadResult> DownloadAsync(
            string url, string destination, string sha256, CancellationToken ct)
        {
            File.WriteAllBytes(destination, _bytes);
            return Task.FromResult(UpdatePackageDownloadResult.Ok());
        }
    }

    /// <summary>Records the launch instead of performing it — no child process is ever started.</summary>
    private sealed class RecordingLauncher : IChildInstallerLauncher
    {
        public bool Called { get; private set; }

        public Task<int> RunAsync(string exePath, IReadOnlyList<string> args, CancellationToken ct)
        {
            Called = true;
            return Task.FromResult(0);
        }
    }
}
