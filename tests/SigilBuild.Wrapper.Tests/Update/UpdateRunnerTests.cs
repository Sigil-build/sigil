using System;
using System.Collections.Generic;
using System.Security.Cryptography;
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
/// T12.3: the headless <c>/Update</c> decision logic behind the fetch / download /
/// child-launch seams. Feeds tampered / malformed manifests through the REAL parser
/// (SIG0320) + verifier (SIG0321) and drives the P3 version comparison with a
/// stubbed installed version — no network, no child process. The live
/// fetch→download→run-child leg is CI-VM-only (T12.6); here we assert the exit-code
/// decision table and the child invocation the seam is handed (scope flag + /silent).
/// </summary>
public class UpdateRunnerTests
{
    private const string ManifestUrl = "https://updates.example.com/acme/stable.json";

    /// <summary>A well-formed but meaningless digest, for the shape-only theory below.</summary>
    private const string GoodSha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    /// <summary>
    /// The bytes <see cref="FakeDownloader"/> puts on disk, and their real digest. The
    /// double must leave a file the runner can re-open and re-verify (R12): the runner
    /// now holds the staged package open across the child launch, so a downloader that
    /// reports success without writing anything is no longer a faithful stand-in for a
    /// real one.
    /// </summary>
    private static readonly byte[] PackageBytes = Encoding.UTF8.GetBytes("the-downloaded-setup-payload");

    private static readonly string PackageSha256 =
        Convert.ToHexString(SHA256.HashData(PackageBytes)).ToLowerInvariant();

    // ── Test doubles (no mocking framework) ──────────────────────────────────

    private sealed class FakeFetcher : IUpdateResourceFetcher
    {
        private readonly Func<string, UpdateResourceResult> _map;
        public List<string> Requested { get; } = new();

        public FakeFetcher(Func<string, UpdateResourceResult> map) => _map = map;

        public Task<UpdateResourceResult> FetchAsync(string url, CancellationToken ct)
        {
            Requested.Add(url);
            return Task.FromResult(_map(url));
        }
    }

    private sealed class FakeDownloader : IUpdatePackageDownloader
    {
        private readonly UpdatePackageDownloadResult _result;
        public bool Called { get; private set; }
        public string? Url { get; private set; }
        public string? Destination { get; private set; }
        public string? Sha256 { get; private set; }

        public FakeDownloader(UpdatePackageDownloadResult result) => _result = result;

        public Task<UpdatePackageDownloadResult> DownloadAsync(string url, string destination, string sha256, CancellationToken ct)
        {
            Called = true;
            Url = url;
            Destination = destination;
            Sha256 = sha256;
            if (_result.Success)
            {
                // A successful download leaves the verified bytes on disk — the runner
                // re-opens and re-hashes them before it launches anything.
                System.IO.File.WriteAllBytes(destination, PackageBytes);
            }
            return Task.FromResult(_result);
        }
    }

    private sealed class FakeLauncher : IChildInstallerLauncher
    {
        private readonly int _exitCode;
        public bool Called { get; private set; }
        public string? ExePath { get; private set; }
        public IReadOnlyList<string>? Args { get; private set; }

        public FakeLauncher(int exitCode) => _exitCode = exitCode;

        public Task<int> RunAsync(string exePath, IReadOnlyList<string> args, CancellationToken ct)
        {
            Called = true;
            ExePath = exePath;
            Args = args;
            return Task.FromResult(_exitCode);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// R13: the freshness fields are REQUIRED, so every fixture manifest carries them.
    /// The defaults are deliberately "minted just now, valid for a week, sequence 1" —
    /// a fixture that never had to think about freshness behaves exactly as it did
    /// before R13, and a test that IS about freshness passes explicit values.
    /// </summary>
    private static (byte[] Manifest, byte[] Signature, string PublicKeyBase64) SignedManifest(
        string version,
        string? sha256 = null,
        string? minFromVersion = null,
        DateTimeOffset? issuedAt = null,
        DateTimeOffset? expiresAt = null,
        long sequence = 1)
    {
        sha256 ??= PackageSha256;
        var issued = issuedAt ?? DateTimeOffset.UtcNow;
        var expires = expiresAt ?? issued.AddDays(7);
        var minPart = minFromVersion is null ? string.Empty : $",\n  \"minFromVersion\": \"{minFromVersion}\"";
        var json =
            "{\n" +
            "  \"schemaVersion\": 1,\n" +
            $"  \"version\": \"{version}\",\n" +
            $"  \"packageUrl\": \"https://updates.example.com/acme/{version}/Setup.exe\",\n" +
            $"  \"issuedAt\": \"{issued:O}\",\n" +
            $"  \"expiresAt\": \"{expires:O}\",\n" +
            $"  \"sequence\": {sequence},\n" +
            $"  \"sha256\": \"{sha256}\"{minPart}\n" +
            "}";
        var bytes = Encoding.UTF8.GetBytes(json);
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
        var sig = ecdsa.SignData(bytes, HashAlgorithmName.SHA256);
        return (bytes, Encoding.UTF8.GetBytes(Convert.ToBase64String(sig)), spki);
    }

    private static string NewPublicKeyBase64()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
    }

    private static FakeFetcher Fetcher(byte[] manifest, byte[] signature) =>
        new(url => url.EndsWith(".sig", StringComparison.Ordinal)
            ? UpdateResourceResult.Ok(signature)
            : UpdateResourceResult.Ok(manifest));

    private static UpgradeState Installed(string version) =>
        new(Found: true, InstalledVersion: version,
            PriorInstallDir: @"C:\Acme", PriorUninstallExe: @"C:\Acme\uninstall.exe",
            FoundScope: InstallScope.Machine);

    private static UpdateRunner Runner(
        IUpdateResourceFetcher fetcher,
        IUpdatePackageDownloader downloader,
        IChildInstallerLauncher launcher,
        UpgradeState installed,
        List<string> log,
        IUpdateSequenceStore? sequences = null) =>
        // R13: never the production sequence store — it reads and WRITES a real
        // %ProgramData% path, and CI runs elevated.
        new(fetcher, downloader, launcher, () => installed, (m, _) => log.Add(m),
            sequences ?? new Helpers.UpdateFixtures.InMemorySequenceStore());

    private static UpdateRequest Request(string? signingKey, InstallScope scope = InstallScope.Machine) =>
        new(ManifestUrl: ManifestUrl, SigningKey: signingKey, Channel: "stable",
            Scope: scope, AppId: "com.acme.Studio", TempDirectory: System.IO.Path.GetTempPath());

    // ── 1. Not update-enabled ─────────────────────────────────────────────────

    [Fact]
    public async Task No_manifest_url_returns_not_configured_and_not_64()
    {
        var fetcher = Fetcher(Array.Empty<byte>(), Array.Empty<byte>());
        var downloader = new FakeDownloader(UpdatePackageDownloadResult.Ok());
        var launcher = new FakeLauncher(0);
        var runner = Runner(fetcher, downloader, launcher, UpgradeState.None, new List<string>());

        var request = new UpdateRequest(
            ManifestUrl: null, SigningKey: "k", Channel: null,
            Scope: InstallScope.Machine, AppId: "com.acme.Studio", TempDirectory: System.IO.Path.GetTempPath());

        var code = await runner.RunAsync(request, CancellationToken.None);

        code.Should().Be(InstallSession.UpdateNotConfiguredExitCode);
        code.Should().NotBe(64);
        fetcher.Requested.Should().BeEmpty();
    }

    // ── 2. Up to date (not newer) → exit 0, no download ───────────────────────

    [Theory]
    [InlineData("2.0.0")] // installed newer than channel 1.0.0 → DowngradeBlocked → up to date
    [InlineData("1.0.0")] // installed == channel → Same → up to date
    public async Task Not_newer_exits_0_without_downloading(string installedVersion)
    {
        var (manifest, sig, key) = SignedManifest("1.0.0");
        var downloader = new FakeDownloader(UpdatePackageDownloadResult.Ok());
        var launcher = new FakeLauncher(0);
        var log = new List<string>();
        var runner = Runner(Fetcher(manifest, sig), downloader, launcher, Installed(installedVersion), log);

        var code = await runner.RunAsync(Request(key), CancellationToken.None);

        code.Should().Be(0);
        downloader.Called.Should().BeFalse();
        launcher.Called.Should().BeFalse();
        string.Join("\n", log).Should().Contain("up to date");
    }

    // ── 3. Newer available → download + run child silently, propagate exit code ─

    [Theory]
    [InlineData(InstallScope.Machine, "/allusers")]
    [InlineData(InstallScope.User, "/currentuser")]
    public async Task Newer_downloads_and_runs_child_silently_propagating_exit_code(
        InstallScope scope, string expectedScopeFlag)
    {
        var (manifest, sig, key) = SignedManifest("2.0.0");
        var downloader = new FakeDownloader(UpdatePackageDownloadResult.Ok());
        var launcher = new FakeLauncher(3010); // child asked for a reboot
        var log = new List<string>();
        var runner = Runner(Fetcher(manifest, sig), downloader, launcher, Installed("1.0.0"), log);

        var code = await runner.RunAsync(Request(key, scope), CancellationToken.None);

        code.Should().Be(3010, "the child installer's exit code is propagated verbatim");
        downloader.Called.Should().BeTrue();
        downloader.Url.Should().Be("https://updates.example.com/acme/2.0.0/Setup.exe");
        downloader.Sha256.Should().Be(PackageSha256);
        launcher.Called.Should().BeTrue();
        launcher.ExePath.Should().Be(downloader.Destination);
        launcher.Args.Should().Equal(expectedScopeFlag, "/silent");
    }

    // ── 3b. T12.4: SilentChild threads through to the child's launch args ─────

    [Fact]
    public async Task SilentChild_defaults_to_true_when_unspecified()
    {
        // Every T12.3-era call site (and every test above) constructs UpdateRequest
        // without naming SilentChild — the default must keep launching the child
        // /silent, exactly as before T12.4.
        var request = Request(NewPublicKeyBase64());
        request.SilentChild.Should().BeTrue();
    }

    [Fact]
    public async Task Headed_update_SilentChild_false_launches_the_child_without_slash_silent()
    {
        // T12.4: a headed, non-silent /Update launches the downloaded child WITHOUT
        // /silent so the user sees its own install wizard — only the scope flag is
        // forwarded.
        var (manifest, sig, key) = SignedManifest("2.0.0");
        var downloader = new FakeDownloader(UpdatePackageDownloadResult.Ok());
        var launcher = new FakeLauncher(0);
        var log = new List<string>();
        var runner = Runner(Fetcher(manifest, sig), downloader, launcher, Installed("1.0.0"), log);

        var request = Request(key) with { SilentChild = false };
        var code = await runner.RunAsync(request, CancellationToken.None);

        code.Should().Be(0);
        launcher.Called.Should().BeTrue();
        launcher.Args.Should().Equal("/allusers");
        launcher.Args.Should().NotContain("/silent");
        string.Join("\n", log).Should().NotContain("/silent",
            "the installing-stage log line must also reflect the headed (no /silent) child launch");
    }

    // ── 3b. R14: a non-https manifestUrl is refused before anything is fetched ─

    /// <summary>
    /// Register row R14, runtime half. SIG0324 catches this at pack time; this is not
    /// redundant with it, because the detached-signature URL is <c>manifestUrl + ".sig"</c>
    /// — so a cleartext manifestUrl silently drags the signature fetch onto cleartext too
    /// — and because installers stamped before SIG0324 existed are already in the field.
    /// </summary>
    [Theory]
    [InlineData("http://updates.example.com/acme/stable.json")]
    [InlineData("ftp://updates.example.com/acme/stable.json")]
    public async Task A_non_https_manifest_url_is_refused_without_fetching(string insecureUrl)
    {
        var (manifest, sig, key) = SignedManifest("2.0.0");
        var fetcher = Fetcher(manifest, sig);
        var downloader = new FakeDownloader(UpdatePackageDownloadResult.Ok());
        var log = new List<string>();
        var runner = Runner(fetcher, downloader, new FakeLauncher(0), Installed("1.0.0"), log);

        var code = await runner.RunAsync(
            new UpdateRequest(
                ManifestUrl: insecureUrl, SigningKey: key, Channel: "stable",
                Scope: InstallScope.Machine, AppId: "com.acme.Studio",
                TempDirectory: System.IO.Path.GetTempPath()),
            CancellationToken.None);

        code.Should().NotBe(0);
        fetcher.Requested.Should().BeEmpty(
            "the refusal must come BEFORE the request — validating after the fetch has already " +
            "leaked the app id, version and channel over cleartext");
        string.Join("\n", log).Should().Contain("https");
    }

    // ── 4. Malformed channel manifest → verified first, so SIG0321 (R39) ──────

    /// <summary>
    /// Register row R39: verify BEFORE parse. This test previously asserted SIG0320 —
    /// i.e. that unverified network bytes reached the JSON parser first. They must not.
    /// A malformed, unsigned document is a SIGNATURE failure, because that is the first
    /// question asked of it; whoever answered the request does not get to choose which
    /// diagnostic the user sees, and the parser is not exposed to unauthenticated input.
    /// </summary>
    [Fact]
    public async Task Malformed_unsigned_manifest_is_rejected_as_a_signature_failure_not_a_parse_failure()
    {
        var bad = Encoding.UTF8.GetBytes("this is not json");
        var downloader = new FakeDownloader(UpdatePackageDownloadResult.Ok());
        var launcher = new FakeLauncher(0);
        var log = new List<string>();
        var runner = Runner(Fetcher(bad, Encoding.UTF8.GetBytes("sig")), downloader, launcher, Installed("1.0.0"), log);

        var code = await runner.RunAsync(Request(NewPublicKeyBase64()), CancellationToken.None);

        code.Should().Be(
            InstallSession.UpdateManifestRejectedExitCode,
            "an unverifiable manifest is a HARD reject, not a soft 'could not check'");
        downloader.Called.Should().BeFalse();
        var joined = string.Join("\n", log);
        joined.Should().Contain(
            "SIG0321",
            "verification must run BEFORE the parse — the signature is what this document fails first");
        joined.Should().NotContain(
            "SIG0320",
            "the JSON parser must never have seen these unverified bytes, so it cannot have " +
            "produced a diagnostic about them");
    }

    // ── 5. Invalid signature (real verifier, SIG0321) → HARD reject ────────────

    [Fact]
    public async Task Invalid_signature_hard_rejects_and_does_not_download()
    {
        var (manifest, sig, _) = SignedManifest("2.0.0");
        var downloader = new FakeDownloader(UpdatePackageDownloadResult.Ok());
        var launcher = new FakeLauncher(0);
        var log = new List<string>();
        var runner = Runner(Fetcher(manifest, sig), downloader, launcher, Installed("1.0.0"), log);

        // Verify against a DIFFERENT key than the one that signed the manifest.
        var code = await runner.RunAsync(Request(NewPublicKeyBase64()), CancellationToken.None);

        code.Should().Be(InstallSession.UpdateManifestRejectedExitCode);
        downloader.Called.Should().BeFalse();
        launcher.Called.Should().BeFalse();
        string.Join("\n", log).Should().Contain("SIG0321");
    }

    // ── 6. Below MinFromVersion floor → not eligible ──────────────────────────

    [Fact]
    public async Task Installed_below_min_from_version_is_not_eligible()
    {
        var (manifest, sig, key) = SignedManifest("3.0.0", minFromVersion: "2.0.0");
        var downloader = new FakeDownloader(UpdatePackageDownloadResult.Ok());
        var launcher = new FakeLauncher(0);
        var log = new List<string>();
        var runner = Runner(Fetcher(manifest, sig), downloader, launcher, Installed("1.0.0"), log);

        var code = await runner.RunAsync(Request(key), CancellationToken.None);

        code.Should().Be(InstallSession.UpdateNotEligibleExitCode);
        downloader.Called.Should().BeFalse();
        string.Join("\n", log).Should().Contain("minimum");
    }

    // ── 6b. R37: a malformed installed version is NOT eligible for a floored ──
    // ── package — an unevaluable floor is not a satisfied floor ───────────────

    /// <summary>
    /// Register row R37. This test previously asserted the opposite — that an
    /// incomparable installed version SKIPPED the floor and the update proceeded.
    /// That is the defect: for a user-scope install the installed version is read from
    /// the user's own HKCU, so anything that can make it unparseable can opt out of a
    /// floor the publisher declared. A floor that cannot be evaluated has not been
    /// satisfied.
    /// </summary>
    [Fact]
    public async Task Malformed_installed_version_is_not_eligible_for_a_floored_package()
    {
        var (manifest, sig, key) = SignedManifest("3.0.0", minFromVersion: "2.0.0");
        var downloader = new FakeDownloader(UpdatePackageDownloadResult.Ok());
        var launcher = new FakeLauncher(0);
        var log = new List<string>();
        // UpgradePlanner treats an unparseable installed version as "older" (Upgrade),
        // so the run does reach the MinFromVersion gate. What happens there is the row.
        var runner = Runner(Fetcher(manifest, sig), downloader, launcher, Installed("not-a-version"), log);

        var code = await runner.RunAsync(Request(key), CancellationToken.None);

        code.Should().Be(
            InstallSession.UpdateNotEligibleExitCode,
            "an installed version that cannot be compared against the declared minFromVersion floor " +
            "must be treated as NOT eligible — skipping the floor lets a user who can corrupt their " +
            "own recorded version take a package the publisher said they may not take");
        downloader.Called.Should().BeFalse(
            "nothing should be downloaded for an update the floor makes ineligible");
        var joined = string.Join("\n", log);
        joined.Should().Contain("minimum", "the refusal must name the floor it is enforcing");
        joined.Should().Contain("not-a-version", "the log should name the unparseable installed version");
    }

    // ── 7. Implausible sha256 → check failed (before spending a download) ──────

    [Fact]
    public async Task Implausible_sha256_returns_check_failed()
    {
        var (manifest, sig, key) = SignedManifest("2.0.0", sha256: "nothex");
        var downloader = new FakeDownloader(UpdatePackageDownloadResult.Ok());
        var launcher = new FakeLauncher(0);
        var runner = Runner(Fetcher(manifest, sig), downloader, launcher, Installed("1.0.0"), new List<string>());

        var code = await runner.RunAsync(Request(key), CancellationToken.None);

        code.Should().Be(InstallSession.UpdateCheckFailedExitCode);
        downloader.Called.Should().BeFalse();
    }

    // ── 8. Download failure → check failed, no child launch ───────────────────

    [Fact]
    public async Task Download_failure_returns_check_failed()
    {
        var (manifest, sig, key) = SignedManifest("2.0.0");
        var downloader = new FakeDownloader(UpdatePackageDownloadResult.Failed("sha256 mismatch"));
        var launcher = new FakeLauncher(0);
        var runner = Runner(Fetcher(manifest, sig), downloader, launcher, Installed("1.0.0"), new List<string>());

        var code = await runner.RunAsync(Request(key), CancellationToken.None);

        code.Should().Be(InstallSession.UpdateCheckFailedExitCode);
        launcher.Called.Should().BeFalse();
    }

    // ── 9. Network failure fetching the manifest → check failed ───────────────

    [Fact]
    public async Task Manifest_fetch_failure_returns_check_failed()
    {
        var fetcher = new FakeFetcher(_ => UpdateResourceResult.Failed("connection refused"));
        var downloader = new FakeDownloader(UpdatePackageDownloadResult.Ok());
        var launcher = new FakeLauncher(0);
        var runner = Runner(fetcher, downloader, launcher, Installed("1.0.0"), new List<string>());

        var code = await runner.RunAsync(Request(NewPublicKeyBase64()), CancellationToken.None);

        code.Should().Be(InstallSession.UpdateCheckFailedExitCode);
        downloader.Called.Should().BeFalse();
    }

    // ── Secret hygiene: the signing key / signature bytes never reach the log ──
    // (extends the SecretHygieneTests / InstallLoggingTests redaction guarantee
    // to the /Update path, per ChannelManifestVerifier's own "redaction-safe"
    // remarks — this proves that promise holds through UpdateRunner's full log.)

    [Fact]
    public async Task Log_never_contains_the_signing_key_on_an_invalid_signature()
    {
        var (manifest, sig, signedWithKey) = SignedManifest("2.0.0");
        var downloader = new FakeDownloader(UpdatePackageDownloadResult.Ok());
        var launcher = new FakeLauncher(0);
        var log = new List<string>();
        var runner = Runner(Fetcher(manifest, sig), downloader, launcher, Installed("1.0.0"), log);

        // Verify against a DIFFERENT key than the one that actually signed the
        // manifest — a SIG0321 hard reject, exercising the verifier's failure path.
        var wrongKey = NewPublicKeyBase64();
        var code = await runner.RunAsync(Request(wrongKey), CancellationToken.None);

        code.Should().Be(InstallSession.UpdateManifestRejectedExitCode);
        var joined = string.Join("\n", log);
        joined.Should().NotContain(wrongKey, "the signing key configured for this run must never reach the log");
        joined.Should().NotContain(signedWithKey, "the manifest's own signing key must never reach the log either");
        joined.Should().NotContain(
            Encoding.UTF8.GetString(sig), "the raw base64 signature bytes must never reach the log");
    }

    [Fact]
    public async Task Log_never_contains_the_signing_key_or_signature_on_a_successful_update()
    {
        var (manifest, sig, key) = SignedManifest("2.0.0");
        var downloader = new FakeDownloader(UpdatePackageDownloadResult.Ok());
        var launcher = new FakeLauncher(0);
        var log = new List<string>();
        var runner = Runner(Fetcher(manifest, sig), downloader, launcher, Installed("1.0.0"), log);

        var code = await runner.RunAsync(Request(key), CancellationToken.None);

        code.Should().Be(0);
        var joined = string.Join("\n", log);
        joined.Should().NotContain(key, "the signing key (updates.signingKey) must never reach the log");
        joined.Should().NotContain(
            Encoding.UTF8.GetString(sig), "the raw base64 signature bytes must never reach the log");
    }

    // ── IsPlausibleSha256Hex unit coverage ────────────────────────────────────

    [Theory]
    [InlineData(GoodSha256, true)]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789", true)]
    [InlineData("nothex", false)]
    [InlineData("aaaa", false)]
    [InlineData(null, false)]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg", false)]
    public void IsPlausibleSha256Hex_classifies_correctly(string? value, bool expected)
        => UpdateRunner.IsPlausibleSha256Hex(value).Should().Be(expected);
}
