using System;
using System.IO;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Win32;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;
using Xunit;

namespace SigilBuild.Wrapper.IntegrationTests;

/// <summary>
/// End-to-end version-aware upgrade legs (G3), appended to the wrapper VM matrix. Packs
/// a self-contained fixture at two versions (a unique app id per run, so the real ARP
/// registry is never polluted across runs) and drives the four decision paths against a
/// real install:
/// <list type="bullet">
///   <item><description>v1 → v2 upgrade: one ARP row, v2 version, prior install dir preserved
///   even when v2's manifest default differs;</description></item>
///   <item><description>v2 → v1 silent: blocked with the dedicated exit code (3);</description></item>
///   <item><description>v1 <c>/force-downgrade</c>: succeeds.</description></item>
/// </list>
/// Reports a genuine Skipped result (via <see cref="VmUpgradeFactAttribute"/>, R6) unless
/// Windows + <c>SIGIL_VM_TESTS=1</c> + <c>SIGIL_VM_UPGRADE=1</c> + the staged AOT runtime
/// — same convention as <see cref="MultiEditionInstallTests"/>. The pure four-path
/// decision table and the prior-dir precedence are additionally covered by fast unit
/// tests (<c>UpgradePlannerTests</c>, <c>InstallDirResolverTests</c>, <c>UpgradeSessionTests</c>).
/// </summary>
/// <remarks>
/// <para>Two of the three legs additionally require an UNELEVATED process
/// (<see cref="VmUpgradeUnelevatedFactAttribute"/>, which documents the mechanism in
/// full): <c>InstalledStateResolver.ScopeProbeOrder</c> probes HKLM only when elevated
/// (R2), so an elevated per-user install cannot see its own prior per-user install and
/// the plan is always <c>FreshInstall</c>. On a GitHub-hosted runner — always elevated —
/// the downgrade guard is therefore silently off and the prior install dir is not
/// preserved; those two assertions skip there with a reason naming R2 and the elevated
/// per-user upgrade-plan row, rather than failing or passing for the wrong reason. That
/// gap is filed as R74, and the prior-version <c>uninstall.exe</c> each of these legs
/// spawns is itself admitted past the single-instance guard via R76's parent-identity
/// handoff, not by relaxing the guard.</para>
/// <para>The fixture ships a per-version marker file (<c>version-&lt;version&gt;.marker</c>,
/// copied by <c>from: payload://**</c>) so a genuine replacement is observable rather
/// than inferred: an exit code and an ARP string alone do not distinguish it from a
/// fresh install layered on top.</para>
/// </remarks>
public sealed class UpgradeInstallTests
{
    private const string UninstallRoot =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    [VmUpgradeUnelevatedFact]
    [SupportedOSPlatform("windows")]
    public async Task Upgrade_replaces_older_version_preserving_install_dir_and_single_arp_row()
    {
        using var sandbox = new VmSandbox();
        var appId = NewAppId();
        // v1 installs into dir A; v2's manifest default is a DIFFERENT dir B. The
        // upgrade must land in A (prior dir wins) — proving install_dir preservation.
        // Prior-dir preservation reads `priorInstallDir` off the ARP-sourced installed
        // state, which is why this leg requires an unelevated process (R2).
        var dirA = Path.Combine(sandbox.Root, "A");
        var dirB = Path.Combine(sandbox.Root, "B");
        try
        {
            var v1 = await PackFixtureAsync(sandbox, appId, "1.0.0", dirA);
            (await sandbox.RunAsync(v1, "/S", "/currentuser")).Should().Be(0);
            File.Exists(Path.Combine(dirA, "app.txt")).Should().BeTrue("v1 installs into its own dir A");
            File.Exists(Path.Combine(dirA, MarkerName("1.0.0"))).Should().BeTrue("v1 ships its own marker");
            ReadArp(appId, "DisplayVersion").Should().Be("1.0.0");

            var v2 = await PackFixtureAsync(sandbox, appId, "2.0.0", dirB);
            (await sandbox.RunAsync(v2, "/S", "/currentuser")).Should().Be(0);

            // Prior dir A preserved even though v2's default is B.
            File.Exists(Path.Combine(dirA, "app.txt")).Should().BeTrue("the upgrade honors the prior install dir A");
            Directory.Exists(dirB).Should().BeFalse("v2 must NOT install into its differing default dir B");
            ReadArp(appId, "DisplayVersion").Should().Be("2.0.0", "the ARP row reflects the upgraded version");

            // The upgrade REPLACED v1 rather than layering v2 over it.
            File.Exists(Path.Combine(dirA, MarkerName("2.0.0"))).Should().BeTrue("v2's payload landed");
            File.Exists(Path.Combine(dirA, MarkerName("1.0.0"))).Should().BeFalse(
                "the upgrade removed the prior version's files — a v2 layered on top of a " +
                "still-present v1 would leave v1's marker behind");
        }
        finally
        {
            Cleanup(appId);
        }
    }

    [VmUpgradeUnelevatedFact]
    [SupportedOSPlatform("windows")]
    public async Task Silent_downgrade_is_blocked_with_exit_code_3()
    {
        using var sandbox = new VmSandbox();
        var appId = NewAppId();
        var dir = Path.Combine(sandbox.Root, "app");
        try
        {
            var v2 = await PackFixtureAsync(sandbox, appId, "2.0.0", dir);
            (await sandbox.RunAsync(v2, "/S", "/currentuser")).Should().Be(0);

            var v1 = await PackFixtureAsync(sandbox, appId, "1.0.0", dir);
            var rc = await sandbox.RunAsync(v1, "/S", "/currentuser");

            rc.Should().Be(3, "installing an older version over a newer one is blocked");
            ReadArp(appId, "DisplayVersion").Should().Be("2.0.0", "the newer install is untouched by the blocked downgrade");
            File.Exists(Path.Combine(dir, MarkerName("2.0.0"))).Should().BeTrue(
                "a blocked downgrade must leave the newer version's files alone");
            File.Exists(Path.Combine(dir, MarkerName("1.0.0"))).Should().BeFalse(
                "the blocked downgrade installed nothing");
        }
        finally
        {
            Cleanup(appId);
        }
    }

    /// <summary>
    /// <c>/force-downgrade</c> overrides the block and the older version really does
    /// take the newer one's place.
    /// </summary>
    /// <remarks>
    /// Asserts the filesystem, not just an exit code and an ARP string: v1's payload must
    /// be in place and v2's marker must be GONE, which a fresh install layered on top of
    /// a surviving v2 would not achieve. Stays on <see cref="VmUpgradeFactAttribute"/>
    /// rather than the unelevated variant, since its assertions hold in an elevated
    /// session too — but via a different route: with the prior install invisible to the
    /// plan (R2), the run is classified <c>FreshInstall</c>, and the state-store-sourced
    /// (not elevation-blind) reinstall cleanup replays v2's recorded uninstall and removes
    /// its files anyway. Same observable outcome, different code path — the elevated
    /// per-user upgrade-plan row. The forced-downgrade decision itself is gated by
    /// <c>UpgradePlannerTests</c>.
    /// </remarks>
    [VmUpgradeFact]
    [SupportedOSPlatform("windows")]
    public async Task Force_downgrade_replaces_the_newer_version()
    {
        using var sandbox = new VmSandbox();
        var appId = NewAppId();
        var dir = Path.Combine(sandbox.Root, "app");
        try
        {
            var v2 = await PackFixtureAsync(sandbox, appId, "2.0.0", dir);
            (await sandbox.RunAsync(v2, "/S", "/currentuser")).Should().Be(0);
            File.Exists(Path.Combine(dir, MarkerName("2.0.0"))).Should().BeTrue(
                "the newer version must be really installed before a downgrade over it " +
                "means anything");

            var v1 = await PackFixtureAsync(sandbox, appId, "1.0.0", dir);
            var rc = await sandbox.RunAsync(v1, "/S", "/currentuser", "/force-downgrade");

            rc.Should().Be(0, "/force-downgrade overrides the block");
            ReadArp(appId, "DisplayVersion").Should().Be("1.0.0", "the forced downgrade installed the older version");

            File.Exists(Path.Combine(dir, MarkerName("1.0.0"))).Should().BeTrue(
                "the older version's payload landed");
            var installed = await File.ReadAllTextAsync(Path.Combine(dir, "app.txt"));
            installed.Trim().Should().Be(
                "version 1.0.0",
                "the file the newer version had written was overwritten by the older " +
                "version's copy — an exit code and an ARP row alone do not show that");
            File.Exists(Path.Combine(dir, MarkerName("2.0.0"))).Should().BeFalse(
                "REPLACED, not layered on top: the newer version's files are gone. This is " +
                "the assertion that distinguishes a real replacement from an install that " +
                "simply wrote over the parts it happened to share");
        }
        finally
        {
            Cleanup(appId);
        }
    }

    /// <summary>
    /// A schema-valid unique app id for one run. The <c>r</c> prefix keeps the trailing
    /// hex segment letter-led, since <c>app.id</c>'s schema pattern
    /// (<c>^[A-Za-z][A-Za-z0-9]*(\.[A-Za-z][A-Za-z0-9]*)+$</c>) requires every segment
    /// to be letter-led (R66).
    /// </summary>
    internal static string NewAppId() => "com.sigil.p3.r" + Guid.NewGuid().ToString("N");

    /// <summary>
    /// Build the fixture manifest YAML. Pure: no sandbox, no disk, no packer — so the
    /// always-on <see cref="VmFixtureManifestTests"/> can validate the exact string
    /// this VM leg packs without a staged runtime (R66).
    /// </summary>
    internal static string BuildManifestYaml(string appId, string version, string installDir)
    {
        // $$ raw string: {{...}} interpolates, single braces ({install_dir}) are literal.
        // Every interpolated path goes through Sigil.YamlQuote — single-quoted YAML, the
        // only style that needs no escaping for a Windows path (R66).
        //
        // `to:` is a destination DIRECTORY, never a file name: FileCopyStep does
        // Directory.CreateDirectory(to) then Path.Combine(to, <relative path>) per match,
        // with no single-source-to-single-file branch (see
        // VmFixtureManifestTests.Vm_fixture_file_copy_destinations_are_directories).
        //
        // `from: payload://**` copies the WHOLE payload, so the per-version marker file
        // lands alongside app.txt and lets the tests tell a replacement from an overlay.
        return $$"""
spec: v1.0

app:
  id: {{appId}}
  name: SigilP3Fixture
  version: {{version}}
  publisher: SigilBuild

build:
  source: ./payload

package:
  formats: [exe]
  architectures: [x64]

installer:
  scope: auto
  install_dir: {{Sigil.YamlQuote(installDir)}}

install_steps:
  - id: copy-app
    type: file_copy
    from: 'payload://**'
    to: '{install_dir}'
""";
    }

    /// <summary>
    /// The name of the version-stamped marker file the fixture's payload carries at
    /// <paramref name="version"/>. Its presence or absence after a second install is what
    /// makes "the other version was REPLACED" observable rather than assumed: a real
    /// replacement removes the other version's marker, an install layered on top of a
    /// surviving prior version leaves it in place. Extension-bearing on purpose — it is a
    /// payload FILE name, which is what <c>from:</c> is for; the destination stays
    /// <c>{install_dir}</c>.
    /// </summary>
    internal static string MarkerName(string version) => "version-" + version + ".marker";

    /// <summary>
    /// Write a minimal self-contained fixture (a payload of <c>app.txt</c> plus this
    /// version's <see cref="MarkerName"/> file, and a manifest
    /// targeting <paramref name="installDir"/> via <c>installer.install_dir</c>) and
    /// pack it into a Setup.exe. The <c>payload://</c> source and <c>{install_dir}</c>
    /// destination are the code-verified forms.
    /// </summary>
    private static async Task<string> PackFixtureAsync(
        VmSandbox sandbox, string appId, string version, string installDir)
    {
        var fixtureDir = Path.Combine(sandbox.Root, "fixture-" + version);
        var payloadDir = Path.Combine(fixtureDir, "payload");
        Directory.CreateDirectory(payloadDir);
        File.WriteAllText(Path.Combine(payloadDir, "app.txt"), $"version {version}\n");
        File.WriteAllText(
            Path.Combine(payloadDir, MarkerName(version)), $"marker for {version}\n");

        var manifestPath = Path.Combine(fixtureDir, "sigil.yaml");
        await File.WriteAllTextAsync(manifestPath, BuildManifestYaml(appId, version, installDir))
            .ConfigureAwait(false);

        var outDir = Path.Combine(fixtureDir, "out");
        return await Sigil.PackAsync(manifestPath, outDir).ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadArp(string appId, string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{UninstallRoot}\{appId}");
        return key?.GetValue(valueName) as string;
    }

    /// <summary>
    /// Undo everything an install leaves OUTSIDE the sandbox directory: the HKCU ARP
    /// subtree and the per-user state dir under
    /// <c>%LocalAppData%\Sigil\&lt;appId&gt;</c> — left behind, it is a live prior-install
    /// record the reinstall-cleanup path reads, and since the app id is unique per run it
    /// would accumulate one orphan directory per test. Mirrors
    /// <c>ArpUninstallStringTests.Cleanup</c>.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void Cleanup(string appId)
    {
#pragma warning disable CA1031 // best-effort test cleanup
        try { Registry.CurrentUser.DeleteSubKeyTree($@"{UninstallRoot}\{appId}", throwOnMissingSubKey: false); }
        catch { /* best-effort */ }
        try { UninstallStateStore.Delete(appId, InstallScope.User); }
        catch { /* best-effort */ }
#pragma warning restore CA1031
    }
}
