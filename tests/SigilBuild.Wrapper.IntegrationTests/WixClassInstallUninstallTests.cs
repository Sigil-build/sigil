namespace SigilBuild.Wrapper.IntegrationTests;

using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

/// <summary>
/// WiX-class install + uninstall snapshot-diff integration test
/// (Sprint 5d, WBS 2.30). Exercises the canonical "WiX-class" payload —
/// file copy + 4 registry writes + 2 shortcuts + a post-install registry
/// mark — and asserts that uninstall reverts every observable mutation.
/// </summary>
/// <remarks>
/// <para>Reports a genuine Skipped result (via <see cref="VmFactAttribute"/>, register
/// row R6) when any of the following are missing: the host is not Windows;
/// <c>SIGIL_VM_TESTS=1</c> is not set; the AOT-published wrapper runtime is not staged
/// under <c>runtimes/win-x64/SigilBuild.Wrapper.exe</c>.</para>
///
/// <para>The test uses HKCU (not HKLM) so it never needs admin rights.
/// Real installers would write under HKLM, but exercising HKLM here would
/// gate the test on Administrator and pollute the host machine. HKCU under
/// a unique subkey gives us a snapshot-clean comparison without that cost,
/// and the wrapper code path through <see cref="Microsoft.Win32.RegistryKey"/>
/// is identical for both hives.</para>
///
/// <para><b>Shortcuts.</b> The shipped example manifest now anchors its two
/// shortcuts at the named <c>start_menu</c> / <c>desktop</c> locations (register
/// row R16 / lane S2) — which is correct for an example that documents real
/// shortcut placement, and wrong for this test in two independent ways: the
/// resulting <c>.lnk</c> files land <em>outside</em> <see cref="SnapshotDiffer"/>'s
/// scope (<c>installDir</c> + the HKCU subtree), so the empty-diff assertion
/// could not observe them at all; and creating them would write real entries into
/// the runner's Start Menu and onto its Desktop, which this stage's standard
/// forbids on any host. So the test packs a <em>copy</em> of the example whose two
/// <c>location:</c> values are rewritten to scratch coordinates under
/// <c>{install_dir}</c> — inside the temp install root, inside the snapshot scope,
/// and therefore actually covered by the uninstall assertion. The shipped manifest
/// is not modified, and <see cref="RewriteShortcutLocationsToScratch"/> fails loudly
/// if it ever stops finding the anchors to rewrite.</para>
///
/// <para>Placing a real <c>.lnk</c> in a real shell folder and reverting it on
/// uninstall is a distinct claim from "uninstall is observation-clean", and is not
/// covered here.</para>
/// </remarks>
public class WixClassInstallUninstallTests
{
    private const string ManifestRel = "examples/exe-wrapper/hello-wix-killer/sigil.yaml";
    private const string RegistrySubKey = "Software\\HelloWiXKiller";

    private static string FindManifest()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Sigil.slnx")))
            {
                break;
            }
            dir = Path.GetDirectoryName(dir);
        }
        if (dir is null)
        {
            throw new InvalidOperationException("could not locate Sigil.slnx");
        }
        return Path.Combine(dir, ManifestRel.Replace('/', Path.DirectorySeparatorChar));
    }

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

        var rcInstall = await sandbox.RunAsync(
            setupExe,
            "/S",
            $"/install_dir={installDir}",
            "/registered_user=alice");
        rcInstall.Should().Be(0, "install must succeed");

        var afterInstall = SnapshotDiffer.Take(installDir, RegistrySubKey);
        var installDiff = SnapshotDiffer.Diff(before, afterInstall);
        installDiff.Should().NotBeEmpty("install must change observable state (sanity check)");

        // Uninstall via the wrapper directly — same code path the ARP UninstallString invokes.
        // We invoke the *original* setup.exe (not a separately published uninstaller copy in
        // install_dir) with /S /Uninstall: this scenario doesn't bootstrap a copy, it just
        // runs the uninstall mode of the same packed exe.
        var rcUninstall = await sandbox.RunAsync(setupExe, "/S", "/Uninstall");
        rcUninstall.Should().Be(0, "uninstall must succeed");

        var afterUninstall = SnapshotDiffer.Take(installDir, RegistrySubKey);
        var uninstallDiff = SnapshotDiffer.Diff(before, afterUninstall);
        uninstallDiff.Should().BeEmpty(
            "uninstall must restore the snapshot exactly (diff: " +
            string.Join("\n", uninstallDiff) + ")");
    }
}
