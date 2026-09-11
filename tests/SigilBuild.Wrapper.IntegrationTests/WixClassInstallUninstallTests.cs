namespace SigilBuild.Wrapper.IntegrationTests;

using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

/// <summary>
/// WiX-class install + uninstall snapshot-diff integration test. Exercises the
/// canonical "WiX-class" payload — file copy + 4 registry writes + 2 shortcuts + a
/// post-install registry mark — and asserts that uninstall reverts every observable
/// mutation.
/// </summary>
/// <remarks>
/// <para>Reports a genuine Skipped result (via <see cref="VmFactAttribute"/>, R6) when
/// any of the following are missing: the host is not Windows; <c>SIGIL_VM_TESTS=1</c> is
/// not set; the AOT-published wrapper runtime is not staged under
/// <c>runtimes/win-x64/SigilBuild.Wrapper.exe</c>.</para>
///
/// <para>Uses HKCU (not HKLM) so it never needs admin rights and never pollutes the host
/// machine; the wrapper code path through <see cref="Microsoft.Win32.RegistryKey"/> is
/// identical for both hives.</para>
///
/// <para><b>Shortcuts.</b> The shipped example manifest anchors its two shortcuts at the
/// named <c>start_menu</c> / <c>desktop</c> locations (R16), which land outside
/// <see cref="SnapshotDiffer"/>'s scope (<c>installDir</c> + the HKCU subtree) and would
/// write real entries onto the runner's Start Menu and Desktop. So the test packs a copy
/// of the example whose two <c>location:</c> values are rewritten to scratch coordinates
/// under <c>{install_dir}</c> — inside the snapshot scope and therefore actually covered
/// by the uninstall assertion. The shipped manifest is not modified, and
/// <see cref="RewriteShortcutLocationsToScratch"/> fails loudly if it ever stops finding
/// the anchors to rewrite. Placing a real <c>.lnk</c> in a real shell folder and reverting
/// it on uninstall is a distinct claim from "uninstall is observation-clean", and is not
/// covered here.</para>
/// </remarks>
public class WixClassInstallUninstallTests
{
    internal const string ManifestRel = "examples/exe-wrapper/hello-desktop-app/sigil.yaml";
    private const string RegistrySubKey = "Software\\HelloDesktopApp";

    internal static string FindManifest() => Sigil.RepoPath(ManifestRel);

    /// <summary>
    /// The argv this leg installs with — the single definition the always-on
    /// <see cref="VmFixtureManifestTests"/> parses through the REAL
    /// <c>CommandLineParser</c> so a grammar drift fails in every CI run.
    /// </summary>
    /// <remarks>
    /// Neither <c>/install_dir=&lt;dir&gt;</c> nor <c>/registered_user=alice</c> is a
    /// token in the wrapper's closed grammar: the install dir is <c>/D=</c> and a
    /// declared parameter is <c>/P&lt;name&gt;=</c> (R66).
    /// </remarks>
    internal static string[] SilentInstallArgs(string installDir) =>
        new[] { "/S", "/D=" + installDir, "/Pregistered_user=alice" };

    /// <summary>The argv this leg uninstalls with (see the comment at the call site).</summary>
    internal static string[] SilentUninstallArgs() => new[] { "/S", "/Uninstall" };

    /// <summary>
    /// Copy the example (manifest + <c>payload/</c>, which <c>build.source: ./payload</c>
    /// resolves relative to the manifest) into <paramref name="destDir"/> and rewrite the
    /// two named shortcut anchors to scratch paths under <c>{install_dir}</c>. Returns the
    /// path of the copied manifest.
    /// </summary>
    /// <remarks>
    /// Throws when an anchor it expects to rewrite is absent: if the example ever changes
    /// shape, this test must fail with "the rewrite no longer matches" rather than quietly
    /// pack the untouched manifest and start writing real Start Menu / Desktop shortcuts on
    /// the runner again. The post-rewrite assertion is the belt to that braces — no
    /// <c>location: start_menu</c> or <c>location: desktop</c> may survive into the packed
    /// copy.
    /// </remarks>
    private static string RewriteShortcutLocationsToScratch(string manifestPath, string destDir)
    {
        var srcDir = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        foreach (var src in Directory.EnumerateFiles(srcDir, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(destDir, Path.GetRelativePath(srcDir, src));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(src, dest, overwrite: true);
        }

        var copiedManifest = Path.Combine(destDir, Path.GetFileName(manifestPath));
        var yaml = File.ReadAllText(copiedManifest);

        foreach (var (anchor, scratch) in new[]
        {
            // install_dir itself, not a subdirectory of it: shortcut_create
            // Directory.CreateDirectory()s its location but the journaled
            // RollbackRecord.DeleteShortcut only removes the .lnk, so a scratch
            // SUBdirectory would survive uninstall and show up as a spurious
            // `dir ...: absent -> present` diff. Landing both .lnk files directly
            // in install_dir keeps them inside the snapshot scope and fully
            // reverted. Their names already differ, so they cannot collide.
            ("location: start_menu", "location: \"{install_dir}\""),
            ("location: desktop", "location: \"{install_dir}\""),
        })
        {
            if (!yaml.Contains(anchor, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"WixClass snapshot test: expected to rewrite '{anchor}' in {ManifestRel} to a " +
                    "scratch path inside install_dir, but the manifest no longer contains it. " +
                    "Re-check that the shortcuts this test packs stay inside SnapshotDiffer's " +
                    "scope and off the runner's real Start Menu / Desktop before updating this.");
            }
            yaml = yaml.Replace(anchor, scratch, StringComparison.Ordinal);
        }

        File.WriteAllText(copiedManifest, yaml);
        return copiedManifest;
    }

    [VmFact]
    public async Task WixClass_install_then_uninstall_yields_empty_diff()
    {
        // Re-assert at the call site so the CA1416 platform analyzer is happy
        // narrowing into the [SupportedOSPlatform("windows")] SnapshotDiffer.Take.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var sandbox = new VmSandbox();
        var outDir = Path.Combine(sandbox.Root, "out");

        // Pack a scratch-coordinate copy, never the shipped example: see the class
        // remarks. Both shortcuts must therefore land inside installDir, which is
        // what SnapshotDiffer observes.
        var manifestPath = RewriteShortcutLocationsToScratch(
            FindManifest(), Path.Combine(sandbox.Root, "manifest"));
        var packedYaml = await File.ReadAllTextAsync(manifestPath);
        packedYaml.Should().NotContain("location: start_menu")
            .And.NotContain("location: desktop",
                "no test may create a real Start Menu or Desktop shortcut on the host");

        var setupExe = await Sigil.PackAsync(manifestPath, outDir);

        var installDir = Path.Combine(sandbox.AppDir, "HWK");

        // Pre-install snapshot — file root doesn't exist yet, registry subtree doesn't exist yet.
        var before = SnapshotDiffer.Take(installDir, RegistrySubKey);

        var rcInstall = await sandbox.RunAsync(setupExe, SilentInstallArgs(installDir));
        rcInstall.Should().Be(0, "install must succeed");

        var afterInstall = SnapshotDiffer.Take(installDir, RegistrySubKey);
        var installDiff = SnapshotDiffer.Diff(before, afterInstall);
        installDiff.Should().NotBeEmpty("install must change observable state (sanity check)");

        // Uninstall via the wrapper directly: /S /Uninstall runs the uninstall mode of the
        // same packed exe, not the uninstaller copy dropped in install_dir. This is NOT
        // the same code path the ARP UninstallString invokes — the original setup exe
        // lives OUTSIDE install_dir, so it never meets the files-in-use gate that the
        // dropped uninstall.exe always meets from within (R58). ArpUninstallStringTests
        // covers the registered string itself.
        var rcUninstall = await sandbox.RunAsync(setupExe, SilentUninstallArgs());
        rcUninstall.Should().Be(0, "uninstall must succeed");

        var afterUninstall = SnapshotDiffer.Take(installDir, RegistrySubKey);
        var uninstallDiff = SnapshotDiffer.Diff(before, afterUninstall);
        uninstallDiff.Should().BeEmpty(
            "uninstall must restore the snapshot exactly (diff: " +
            string.Join("\n", uninstallDiff) + ")");
    }
}
