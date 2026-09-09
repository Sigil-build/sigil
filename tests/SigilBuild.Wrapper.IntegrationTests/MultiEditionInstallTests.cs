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
    internal const string ManifestRel = "examples/exe-wrapper/multi-edition/sigil.yaml";

    internal static string FindManifest() => Sigil.RepoPath(ManifestRel);

    /// <summary>
    /// The argv this leg runs the packed Setup.exe with — the single definition the
    /// always-on <see cref="VmFixtureManifestTests"/> parses through the REAL
    /// <c>CommandLineParser</c>, so a grammar drift fails in every CI run instead of
    /// only on a dispatched VM run.
    /// </summary>
    /// <remarks>
    /// R66: this used to be <c>/Edition=enterprise /InstallDir=&lt;dir&gt;</c>. Neither
    /// token exists in the wrapper's closed grammar — a declared parameter is overridden
    /// with <c>/P&lt;name&gt;=&lt;value&gt;</c> and the install dir with <c>/D=</c> — so
    /// both legs died with a <c>UsageException</c> (exit <b>64</b>) on the first real VM
    /// run, before any install happened. The example declares the parameter as
    /// <c>edition</c> (lower case; the parser matches case-insensitively).
    /// </remarks>
    internal static string[] SilentInstallArgs(string edition, string installDir) =>
        new[] { "/S", "/Pedition=" + edition, "/D=" + installDir };

    [VmFact]
    public async Task Pack_install_uninstall_roundtrip_for_enterprise_edition()
    {
        using var sandbox = new VmSandbox();
        var manifestPath = FindManifest();
        var outDir = Path.Combine(sandbox.Root, "out");
        var setupExe = await Sigil.PackAsync(manifestPath, outDir);

        var rc = await sandbox.RunAsync(
            setupExe, SilentInstallArgs("enterprise", sandbox.AppDir));
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
            setupExe, SilentInstallArgs("community", sandbox.AppDir));
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
