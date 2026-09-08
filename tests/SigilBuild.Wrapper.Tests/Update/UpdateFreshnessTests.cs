using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Diagnostics;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Tests.Helpers;
using SigilBuild.Wrapper.Update;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Update;

/// <summary>
/// Register row R13: freshness and replay protection on the signed channel manifest.
/// </summary>
/// <remarks>
/// <para>
/// <b>The threat.</b> The signature proves WHO minted the document, never WHEN. Before
/// this row there was no timestamp, expiry, nonce or sequence anywhere in the manifest,
/// and the only monotonicity check was against the locally installed version. So an
/// on-path attacker or a compromised CDN could replay yesterday's correctly signed
/// manifest forever — the client reports "up to date" and exits 0 while a security fix
/// exists (a freeze attack) — or replay a signed manifest for an intermediate
/// <em>vulnerable</em> version that is still newer than installed, which the client then
/// installs.
/// </para>
/// <para>
/// <b>Which of these fail at the parent commit, and how.</b> The parser tests below
/// name only <see cref="ChannelManifestParser.Parse"/> and string literals, so they
/// compile at <c>b62de86</c> and fail there because the parser accepts a manifest with
/// no freshness fields at all. The end-to-end window test uses the FIVE-argument
/// <see cref="UpdateRunner"/> constructor deliberately, for the same reason — see the
/// note on that test.
/// </para>
/// </remarks>
public class UpdateFreshnessTests
{
    private const string ManifestUrl = "https://updates.example.com/acme/stable.json";

    private static readonly byte[] PackageBytes = Encoding.UTF8.GetBytes("the-downloaded-setup-payload");

    private static readonly string PackageSha256 =
        Convert.ToHexString(SHA256.HashData(PackageBytes)).ToLowerInvariant();

    // ── Parser: the three freshness fields are REQUIRED ───────────────────────

    private static string ManifestJson(
        string? issuedAt = "2026-01-01T00:00:00.0000000+00:00",
        string? expiresAt = "2099-01-01T00:00:00.0000000+00:00",
        string? sequence = "1")
    {
        var parts = new List<string>
        {
            "\"schemaVersion\": 1",
            "\"version\": \"2.0.0\"",
            "\"packageUrl\": \"https://updates.example.com/acme/2.0.0/Setup.exe\"",
            "\"sha256\": \"" + new string('a', 64) + "\"",
        };
        if (issuedAt is not null) parts.Add($"\"issuedAt\": \"{issuedAt}\"");
        if (expiresAt is not null) parts.Add($"\"expiresAt\": \"{expiresAt}\"");
        if (sequence is not null) parts.Add($"\"sequence\": {sequence}");
        return "{" + string.Join(",", parts) + "}";
    }

    [Fact]
    public void A_manifest_with_no_issuedAt_is_malformed()
    {
        var result = ChannelManifestParser.Parse(ManifestJson(issuedAt: null));

        result.Success.Should().BeFalse(
            "a manifest with no issue time cannot be checked for freshness at all, which makes it " +
            "indistinguishable from a replay of an arbitrarily old one");
        result.DiagnosticCode.Should().Be("SIG0320");
        result.Error.Should().Contain("issuedAt");
    }

    [Fact]
    public void A_manifest_with_no_expiresAt_is_malformed()
    {
        var result = ChannelManifestParser.Parse(ManifestJson(expiresAt: null));

        result.Success.Should().BeFalse(
            "a manifest with no expiry stays actionable forever, which is precisely what makes an " +
            "indefinite freeze attack possible");
        result.DiagnosticCode.Should().Be("SIG0320");
        result.Error.Should().Contain("expiresAt");
    }

    [Fact]
    public void A_manifest_with_no_sequence_is_malformed()
    {
        var result = ChannelManifestParser.Parse(ManifestJson(sequence: null));

        result.Success.Should().BeFalse(
            "without a sequence, a manifest that is still inside its own validity window can be " +
            "replayed to roll the client back to a superseded — possibly vulnerable — version");
        result.DiagnosticCode.Should().Be("SIG0320");
        result.Error.Should().Contain("sequence");
    }

    [Theory]
    [InlineData("not-a-timestamp")]
    // Locale-ambiguous: the lenient parser resolves this by culture convention, i.e. to
    // two different days depending on who reads it. A validity window must not.
    [InlineData("01/02/2026")]
    // No offset at all — "midnight, somewhere" is up to 26 hours of slack per window edge.
    [InlineData("2026-01-01T00:00:00")]
    [InlineData("")]
    public void A_manifest_with_an_unparseable_issuedAt_is_malformed(string bad)
    {
        var result = ChannelManifestParser.Parse(ManifestJson(issuedAt: bad));

        result.Success.Should().BeFalse();
        result.DiagnosticCode.Should().Be("SIG0320");
    }

    [Fact]
    public void A_manifest_carrying_all_three_freshness_fields_parses()
    {
        // The positive control: the R13 fix must accept a well-formed fresh manifest.
        var result = ChannelManifestParser.Parse(ManifestJson());

        result.Success.Should().BeTrue();
        result.Manifest!.Sequence.Should().Be(1);
        result.Manifest.IssuedAt.Should().Be("2026-01-01T00:00:00.0000000+00:00");
    }

    // ── The freshness decision itself ─────────────────────────────────────────

    private static ChannelManifest Manifest(
        DateTimeOffset issuedAt, DateTimeOffset expiresAt, long sequence = 1) =>
        new(SchemaVersion: 1,
            Version: "2.0.0",
            PackageUrl: "https://updates.example.com/acme/2.0.0/Setup.exe",
            Sha256: new string('a', 64),
            MinFromVersion: null,
            IssuedAt: issuedAt.ToString("O"),
            ExpiresAt: expiresAt.ToString("O"),
            Sequence: sequence);

    [Fact]
    public void An_expired_manifest_is_refused()
    {
        var now = DateTimeOffset.UtcNow;
        var manifest = Manifest(now.AddDays(-10), now.AddDays(-3));

        var refusal = UpdateRunner.EvaluateFreshness(manifest, lastSequence: null, now);

        refusal.Should().NotBeNull("an expired manifest must not be acted on");
        refusal.Should().Contain("stale");
    }

    [Fact]
    public void A_manifest_older_than_the_client_maximum_age_is_refused_even_if_unexpired()
    {
        // A publisher who sets expiresAt to the far future — by mistake, or to make a
        // warning go away — must not thereby opt out of the whole defence.
        var now = DateTimeOffset.UtcNow;
        var manifest = Manifest(now.AddDays(-400), now.AddYears(50));

        var refusal = UpdateRunner.EvaluateFreshness(manifest, lastSequence: null, now);

        refusal.Should().NotBeNull(
            "the client enforces its own ceiling on manifest age regardless of the expiry the " +
            "document declares");
        refusal.Should().Contain("stale");
    }

    [Fact]
    public void A_future_dated_manifest_is_refused()
    {
        var now = DateTimeOffset.UtcNow;
        var manifest = Manifest(now.AddDays(3), now.AddDays(10));

        var refusal = UpdateRunner.EvaluateFreshness(manifest, lastSequence: null, now);

        refusal.Should().NotBeNull("refusing beats guessing which clock is wrong");
    }

    [Fact]
    public void A_manifest_with_a_lower_sequence_than_last_seen_is_refused()
    {
        var now = DateTimeOffset.UtcNow;
        // Correctly signed, inside its validity window — and still a rollback.
        var manifest = Manifest(now.AddHours(-1), now.AddDays(7), sequence: 4);

        var refusal = UpdateRunner.EvaluateFreshness(manifest, lastSequence: 5, now);

        refusal.Should().NotBeNull(
            "a manifest whose sequence has already been superseded is a replay even though its " +
            "own validity window is still open — this is the case the window alone cannot catch");
        refusal.Should().Contain("4");
        refusal.Should().Contain("5");
    }

    [Fact]
    public void An_equal_sequence_is_accepted_so_a_repeat_check_is_not_a_rollback()
    {
        var now = DateTimeOffset.UtcNow;
        var manifest = Manifest(now.AddHours(-1), now.AddDays(7), sequence: 5);

        UpdateRunner.EvaluateFreshness(manifest, lastSequence: 5, now).Should().BeNull(
            "checking twice against the same current manifest is the normal case, not an attack");
    }

    [Fact]
    public void A_fresh_manifest_on_first_contact_is_accepted()
    {
        var now = DateTimeOffset.UtcNow;
        var manifest = Manifest(now.AddHours(-1), now.AddDays(7), sequence: 1);

        UpdateRunner.EvaluateFreshness(manifest, lastSequence: null, now).Should().BeNull();
    }

    [Theory]
    [InlineData(-4)]  // client clock 4 minutes behind the expiry
    [InlineData(4)]   // client clock 4 minutes ahead of issuance
    public void Clock_skew_within_tolerance_does_not_refuse(int minutes)
    {
        // A window with no skew allowance breaks on any misconfigured clock, and a wrong
        // clock is far more common than an on-path replay.
        var now = DateTimeOffset.UtcNow;
        var manifest = minutes < 0
            ? Manifest(now.AddDays(-1), now.AddMinutes(minutes))
            : Manifest(now.AddMinutes(minutes), now.AddDays(7));

        UpdateRunner.EvaluateFreshness(manifest, lastSequence: null, now).Should().BeNull(
            "a few minutes of clock skew must not break updates");
    }

    // ── End to end through the runner ─────────────────────────────────────────

    private sealed class Fetcher : IUpdateResourceFetcher
    {
        private readonly byte[] _manifest;
        private readonly byte[] _signature;

        public Fetcher(byte[] manifest, byte[] signature)
        {
            _manifest = manifest;
            _signature = signature;
        }

        public Task<UpdateResourceResult> FetchAsync(string url, CancellationToken ct) =>
            Task.FromResult(UpdateResourceResult.Ok(
                url.EndsWith(".sig", StringComparison.Ordinal) ? _signature : _manifest));
    }

    private sealed class RecordingLauncher : IChildInstallerLauncher
    {
        public bool Called { get; private set; }

        public Task<int> RunAsync(string exePath, IReadOnlyList<string> args, CancellationToken ct)
        {
            Called = true;
            return Task.FromResult(0);
        }
    }

    private sealed class RecordingDownloader : IUpdatePackageDownloader
    {
        public bool Called { get; private set; }

        public Task<UpdatePackageDownloadResult> DownloadAsync(
            string url, string destination, string sha256, CancellationToken ct)
        {
            Called = true;
            System.IO.File.WriteAllBytes(destination, PackageBytes);
            return Task.FromResult(UpdatePackageDownloadResult.Ok());
        }
    }

    private static (byte[] Manifest, byte[] Signature, string PublicKeyBase64) SignedJson(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
        var sig = ecdsa.SignData(bytes, HashAlgorithmName.SHA256);
        return (bytes, Encoding.UTF8.GetBytes(Convert.ToBase64String(sig)), spki);
    }

    /// <summary>
    /// The whole row, end to end: a manifest that is signed by the right key, advertises
    /// a genuinely newer version, and is 90 days stale.
    /// </summary>
    /// <remarks>
    /// <b>This test deliberately uses the five-argument <see cref="UpdateRunner"/>
    /// constructor</b> — the one that exists at the parent commit — so it compiles and
    /// fails there. That leaves it on the production sequence store, which is safe HERE
    /// and only here: the run is refused at the freshness gate, which is before any
    /// sequence is recorded, so the store is only ever READ. No other test in this file
    /// may copy that pattern.
    /// </remarks>
    [Fact]
    public async Task A_replayed_expired_manifest_is_rejected_end_to_end()
    {
        var issued = DateTimeOffset.UtcNow.AddDays(-90);
        var json =
            "{\n" +
            "  \"schemaVersion\": 1,\n" +
            "  \"version\": \"2.0.0\",\n" +
            "  \"packageUrl\": \"https://updates.example.com/acme/2.0.0/Setup.exe\",\n" +
            $"  \"issuedAt\": \"{issued:O}\",\n" +
            $"  \"expiresAt\": \"{issued.AddDays(7):O}\",\n" +
            "  \"sequence\": 1,\n" +
            $"  \"sha256\": \"{PackageSha256}\"\n" +
            "}";
        var (manifest, signature, key) = SignedJson(json);

        var downloader = new RecordingDownloader();
        var launcher = new RecordingLauncher();
        var log = new List<string>();
        var runner = new UpdateRunner(
            new Fetcher(manifest, signature),
            downloader,
            launcher,
            () => new UpgradeState(
                Found: true, InstalledVersion: "1.0.0", PriorInstallDir: @"C:\Acme",
                PriorUninstallExe: @"C:\Acme\uninstall.exe", FoundScope: InstallScope.Machine),
            (m, _) => log.Add(m));

        var code = await runner.RunAsync(
            new UpdateRequest(
                ManifestUrl: ManifestUrl, SigningKey: key, Channel: "stable",
                Scope: InstallScope.Machine, AppId: "com.acme.FreshnessTest",
                TempDirectory: System.IO.Path.GetTempPath()),
            CancellationToken.None);

        code.Should().NotBe(
            0,
            "a correctly signed but 90-day-stale manifest is a replay — acting on it is how an " +
            "on-path attacker freezes a client on a version with a known vulnerability");
        downloader.Called.Should().BeFalse("nothing should be downloaded for a manifest already refused");
        launcher.Called.Should().BeFalse();
        string.Join("\n", log).Should().Contain("stale");
    }
}
