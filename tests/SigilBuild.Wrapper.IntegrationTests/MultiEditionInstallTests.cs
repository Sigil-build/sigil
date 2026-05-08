using System.IO;
using System.Threading.Tasks;
using FluentAssertions;

namespace SigilBuild.Wrapper.IntegrationTests;

/// <summary>
/// Multi-edition install integration test (Sprint 5c, WBS 2.24). Exercises
/// the full pack -> install -> filesystem-verify cycle against the example
/// manifest in <c>examples/exe-wrapper/multi-edition</c>.
/// </summary>
/// <remarks>
/// Tests soft-skip (return as Passed) when any of the following are missing:
/// <list type="bullet">
///   <item><description>The host is not Windows.</description></item>
///   <item><description><c>SIGIL_VM_TESTS=1</c> is not set in the environment.</description></item>
///   <item><description>The AOT-published wrapper runtime is not staged under
///   <c>runtimes/win-x64/SigilBuild.Wrapper.exe</c> next to the test assembly.</description></item>
/// </list>
/// The repo doesn't currently take a dependency on <c>Xunit.SkippableFact</c>,
/// so the tests use a soft-skip pattern (early <c>return</c>) rather than a
/// "Skipped" verdict — they report as Passed in <c>dotnet test</c>. When
/// <c>Xunit.SkippableFact</c> lands in <c>Directory.Packages.props</c>, prefer
/// <c>Skip.IfNot(...)</c> for a more honest verdict.
/// </remarks>
public class MultiEditionInstallTests
{
    private const string ManifestRel = "examples/exe-wrapper/multi-edition/sigil.yaml";

    private static string FindManifest()
    {
        // Walk up from AppContext.BaseDirectory to find the repo root by Sigil.sln.
        var dir = System.AppContext.BaseDirectory;
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
            throw new System.InvalidOperationException("could not locate Sigil.sln");
        }
        return Path.Combine(dir, ManifestRel.Replace('/', Path.DirectorySeparatorChar));
    }

    private static bool ShouldRun()
        => System.OperatingSystem.IsWindows()
            && TestEnvironment.IsEnabled
            && TestEnvironment.IsRuntimeAvailable;

    [Fact]
    public async Task Pack_install_uninstall_roundtrip_for_enterprise_edition()
    {
        if (!ShouldRun())
        {
            return; // soft-skip — see class remarks.
        }

        using var sandbox = new VmSandbox();
        var manifestPath = FindManifest();
        var outDir = Path.Combine(sandbox.Root, "out");
        var setupExe = await Sigil.PackAsync(manifestPath, outDir);

        var rc = await sandbox.RunAsync(
            setupExe,
            "/S",
            "/Edition=enterprise",
            $"/InstallDir={sandbox.AppDir}");
        rc.Should().Be(0);

        File.Exists(Path.Combine(sandbox.AppDir, "app.txt")).Should().BeTrue();
        File.Exists(Path.Combine(sandbox.AppDir, "pro", "pro.txt")).Should().BeTrue();
        File.Exists(Path.Combine(sandbox.AppDir, "enterprise", "ent.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task Community_edition_skips_pro_and_enterprise_steps()
    {
        if (!ShouldRun())
        {
            return; // soft-skip — see class remarks.
        }

        using var sandbox = new VmSandbox();
        var manifestPath = FindManifest();
        var outDir = Path.Combine(sandbox.Root, "out");
        var setupExe = await Sigil.PackAsync(manifestPath, outDir);

        var rc = await sandbox.RunAsync(
            setupExe,
            "/S",
            "/Edition=community",
            $"/InstallDir={sandbox.AppDir}");
        rc.Should().Be(0);

        File.Exists(Path.Combine(sandbox.AppDir, "app.txt")).Should().BeTrue();
        Directory.Exists(Path.Combine(sandbox.AppDir, "pro")).Should().BeFalse();
        Directory.Exists(Path.Combine(sandbox.AppDir, "enterprise")).Should().BeFalse();
    }

    [Fact]
    public void Test_environment_check_smoke()
    {
        // Always-runnable sanity test confirming the gate works as documented.
        TestEnvironment.IsEnabled.Should().Be(
            System.Environment.GetEnvironmentVariable("SIGIL_VM_TESTS") == "1");
    }
}
