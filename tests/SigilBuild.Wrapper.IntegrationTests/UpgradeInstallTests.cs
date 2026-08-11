using System;
using System.IO;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Win32;
using Xunit;

namespace SigilBuild.Wrapper.IntegrationTests;

/// <summary>
/// P3 (gap G3) end-to-end version-aware upgrade legs, appended to the wrapper VM
/// matrix. Packs a self-contained fixture at two versions (a unique app id per run,
/// so the real ARP registry is never polluted across runs) and drives the four
/// decision paths against a real install:
/// <list type="bullet">
///   <item><description>v1 → v2 upgrade: one ARP row, v2 version, prior install dir preserved
///   even when v2's manifest default differs;</description></item>
///   <item><description>v2 → v1 silent: blocked with the dedicated exit code (3);</description></item>
///   <item><description>v1 <c>/force-downgrade</c>: succeeds.</description></item>
/// </list>
/// Reports a genuine Skipped result (via <see cref="VmUpgradeFactAttribute"/>, register
/// row R6) unless Windows + <c>SIGIL_VM_TESTS=1</c> + <c>SIGIL_VM_UPGRADE=1</c> + the
/// staged AOT runtime — same convention as <see cref="MultiEditionInstallTests"/>. The
/// pure four-path decision table and the
/// prior-dir precedence are additionally covered by fast unit tests
/// (<c>UpgradePlannerTests</c>, <c>InstallDirResolverTests</c>, <c>UpgradeSessionTests</c>).
/// </summary>
public sealed class UpgradeInstallTests
{
    private const string UninstallRoot =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    [VmUpgradeFact]
    [SupportedOSPlatform("windows")]
    public async Task Upgrade_replaces_older_version_preserving_install_dir_and_single_arp_row()
    {
        using var sandbox = new VmSandbox();
        var appId = "com.sigil.p3." + Guid.NewGuid().ToString("N");
        // v1 installs into dir A; v2's manifest default is a DIFFERENT dir B. The
        // upgrade must land in A (prior dir wins) — proving install_dir preservation.
        var dirA = Path.Combine(sandbox.Root, "A");
        var dirB = Path.Combine(sandbox.Root, "B");
        try
        {
            var v1 = await PackFixtureAsync(sandbox, appId, "1.0.0", dirA);
            (await sandbox.RunAsync(v1, "/S", "/currentuser")).Should().Be(0);
            File.Exists(Path.Combine(dirA, "app.txt")).Should().BeTrue("v1 installs into its own dir A");
            ReadArp(appId, "DisplayVersion").Should().Be("1.0.0");

            var v2 = await PackFixtureAsync(sandbox, appId, "2.0.0", dirB);
            (await sandbox.RunAsync(v2, "/S", "/currentuser")).Should().Be(0);

            // Prior dir A preserved even though v2's default is B.
            File.Exists(Path.Combine(dirA, "app.txt")).Should().BeTrue("the upgrade honors the prior install dir A");
            Directory.Exists(dirB).Should().BeFalse("v2 must NOT install into its differing default dir B");
            ReadArp(appId, "DisplayVersion").Should().Be("2.0.0", "the ARP row reflects the upgraded version");
        }
        finally
        {
            CleanupArp(appId);
        }
    }

    [VmUpgradeFact]
    [SupportedOSPlatform("windows")]
    public async Task Silent_downgrade_is_blocked_with_exit_code_3()
    {
        using var sandbox = new VmSandbox();
        var appId = "com.sigil.p3." + Guid.NewGuid().ToString("N");
        var dir = Path.Combine(sandbox.Root, "app");
        try
        {
            var v2 = await PackFixtureAsync(sandbox, appId, "2.0.0", dir);
            (await sandbox.RunAsync(v2, "/S", "/currentuser")).Should().Be(0);

            var v1 = await PackFixtureAsync(sandbox, appId, "1.0.0", dir);
            var rc = await sandbox.RunAsync(v1, "/S", "/currentuser");

            rc.Should().Be(3, "installing an older version over a newer one is blocked");
            ReadArp(appId, "DisplayVersion").Should().Be("2.0.0", "the newer install is untouched by the blocked downgrade");
        }
        finally
        {
            CleanupArp(appId);
        }
    }

    [VmUpgradeFact]
    [SupportedOSPlatform("windows")]
    public async Task Force_downgrade_replaces_the_newer_version()
    {
        using var sandbox = new VmSandbox();
        var appId = "com.sigil.p3." + Guid.NewGuid().ToString("N");
        var dir = Path.Combine(sandbox.Root, "app");
        try
        {
            var v2 = await PackFixtureAsync(sandbox, appId, "2.0.0", dir);
            (await sandbox.RunAsync(v2, "/S", "/currentuser")).Should().Be(0);

            var v1 = await PackFixtureAsync(sandbox, appId, "1.0.0", dir);
            var rc = await sandbox.RunAsync(v1, "/S", "/currentuser", "/force-downgrade");

            rc.Should().Be(0, "/force-downgrade overrides the block");
            ReadArp(appId, "DisplayVersion").Should().Be("1.0.0", "the forced downgrade installed the older version");
        }
        finally
        {
            CleanupArp(appId);
        }
    }

    /// <summary>
    /// Write a minimal self-contained fixture (a single payload file + a manifest
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

        var installDirYaml = installDir.Replace("\\", "\\\\");
        // $$ raw string: {{...}} interpolates, single braces ({install_dir}) are literal.
        var manifest = $$"""
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
  install_dir: "{{installDirYaml}}"

install_steps:
  - id: copy-app
    type: file_copy
    from: "payload://app.txt"
    to: "{install_dir}\\app.txt"
""";
        var manifestPath = Path.Combine(fixtureDir, "sigil.yaml");
        await File.WriteAllTextAsync(manifestPath, manifest).ConfigureAwait(false);

        var outDir = Path.Combine(fixtureDir, "out");
        return await Sigil.PackAsync(manifestPath, outDir).ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadArp(string appId, string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{UninstallRoot}\{appId}");
        return key?.GetValue(valueName) as string;
    }

    [SupportedOSPlatform("windows")]
    private static void CleanupArp(string appId)
    {
#pragma warning disable CA1031 // best-effort test cleanup
        try { Registry.CurrentUser.DeleteSubKeyTree($@"{UninstallRoot}\{appId}", throwOnMissingSubKey: false); }
        catch { /* best-effort */ }
#pragma warning restore CA1031
    }
}
