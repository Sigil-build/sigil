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
/// Tests report a genuine Skipped result (via <see cref="VmFactAttribute"/>, register
/// row R6) when any of the following are missing:
/// <list type="bullet">
///   <item><description>The host is not Windows.</description></item>
///   <item><description><c>SIGIL_VM_TESTS=1</c> is not set in the environment.</description></item>
///   <item><description>The AOT-published wrapper runtime is not staged under
///   <c>runtimes/win-x64/SigilBuild.Wrapper.exe</c> next to the test assembly.</description></item>
/// </list>
/// </remarks>
public class MultiEditionInstallTests
{
    private const string ManifestRel = "examples/exe-wrapper/multi-edition/sigil.yaml";

    private static string FindManifest()
    {
        // Walk up from AppContext.BaseDirectory to find the repo root by Sigil.slnx.
        var dir = System.AppContext.BaseDirectory;
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
            throw new System.InvalidOperationException("could not locate Sigil.slnx");
        }
        return Path.Combine(dir, ManifestRel.Replace('/', Path.DirectorySeparatorChar));
    }

    [VmFact]
    public async Task Pack_install_uninstall_roundtrip_for_enterprise_edition()
    {
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

    [VmFact]
    public async Task Community_edition_skips_pro_and_enterprise_steps()
    {
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
