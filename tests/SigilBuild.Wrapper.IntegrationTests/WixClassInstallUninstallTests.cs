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
/// <para>Soft-skips (returns Passed) when any of the following are missing:
/// the host is not Windows; <c>SIGIL_VM_TESTS=1</c> is not set; the
/// AOT-published wrapper runtime is not staged under
/// <c>runtimes/win-x64/SigilBuild.Wrapper.exe</c>.</para>
///
/// <para>The test uses HKCU (not HKLM) so it never needs admin rights.
/// Real installers would write under HKLM, but exercising HKLM here would
/// gate the test on Administrator and pollute the host machine. HKCU under
/// a unique subkey gives us a snapshot-clean comparison without that cost,
/// and the wrapper code path through <see cref="Microsoft.Win32.RegistryKey"/>
/// is identical for both hives.</para>
///
/// <para>Shortcuts likewise go into <c>install_dir/StartMenu</c> and
/// <c>install_dir/Desktop</c> rather than the user's actual Start Menu /
/// Desktop, so that they are confined to the temp install root and are
/// snapshot-equal pre/post-uninstall.</para>
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
            if (File.Exists(Path.Combine(dir, "Sigil.sln")))
            {
                break;
            }
            dir = Path.GetDirectoryName(dir);
        }
        if (dir is null)
        {
            throw new InvalidOperationException("could not locate Sigil.sln");
        }
        return Path.Combine(dir, ManifestRel.Replace('/', Path.DirectorySeparatorChar));
    }

    private static bool ShouldRun() =>
        OperatingSystem.IsWindows() &&
        TestEnvironment.IsEnabled &&
        TestEnvironment.IsRuntimeAvailable;

    [Fact]
    public async Task WixClass_install_then_uninstall_yields_empty_diff()
    {
        if (!ShouldRun())
        {
            return; // soft-skip — see class remarks.
        }

        // Re-assert at the call site so the CA1416 platform analyzer is happy
        // narrowing into the [SupportedOSPlatform("windows")] SnapshotDiffer.Take.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var sandbox = new VmSandbox();
        var manifestPath = FindManifest();
        var outDir = Path.Combine(sandbox.Root, "out");
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
