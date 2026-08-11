namespace SigilBuild.Packaging.IntegrationTests;

using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;

/// <summary>
/// VM-style MSIX install smoke test (Sprint 5, WBS 2.10). Packs the
/// <c>examples/msix-local-sign</c> manifest end-to-end, then drives
/// <c>install-msix.ps1</c> with <c>-AllowUnsigned</c> to install + uninstall
/// the produced MSIX via <c>Add-AppxPackage</c>.
/// </summary>
/// <remarks>
/// <para>
/// Reports a genuine Skipped result (via <see cref="MsixVmFactAttribute"/>, register
/// row R6) when:
/// </para>
/// <list type="bullet">
///   <item><description>The host is not Windows.</description></item>
///   <item><description><c>SIGIL_MSIX_VM_TESTS=1</c> is not set in the environment — keeps the test out of stock developer runs where Developer Mode may not be on.</description></item>
/// </list>
/// <para>
/// The test uses <c>-AllowUnsigned</c> which requires Developer Mode on the
/// host. The signed-cert variant lives in <c>setup-test-cert.ps1</c> and is
/// driven manually per the README; running it from CI would require persisting
/// a trusted root cert into LocalMachine, which we deliberately avoid.
/// </para>
/// </remarks>
public class MsixInstallSmokeTests
{
    private const string ManifestRel = "examples/msix-local-sign/sigil.yaml";
    private const string ExpectedAppId = "com.example.LocalSignedApp";

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Sigil.slnx")))
            {
                return dir;
            }
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("could not locate Sigil.slnx");
    }

    [MsixVmFact]
    public async Task Pack_and_install_unsigned_msix_via_AddAppxPackage_succeeds()
    {
        var root = FindRepoRoot();
        var manifestPath = Path.Combine(root, ManifestRel.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(manifestPath).Should().BeTrue($"example manifest must exist at {manifestPath}");

        var outDir = Path.Combine(Path.GetTempPath(), "sigil-msix-smoke-" + Path.GetRandomFileName());
        Directory.CreateDirectory(outDir);
        try
        {
            // Step 1 — pack via the CLI dispatcher (same code path users exercise).
            var packExit = await RunDotnetAsync(root,
                "run", "--project", "src/SigilBuild.Cli", "--",
                "pack", manifestPath, "--out", outDir);
            packExit.Should().Be(0, "sigil pack must succeed for the example manifest");

            var msixPath = Path.Combine(outDir, $"{ExpectedAppId}-1.2.3-x64.msix");
            File.Exists(msixPath).Should().BeTrue($"pack must produce {msixPath}");

            // Step 2 — install + verify + remove via the existing PowerShell harness.
            var installScript = Path.Combine(
                root, "tests", "SigilBuild.Packaging.IntegrationTests", "install-msix.ps1");
            var psExit = await RunPowerShellAsync(
                installScript,
                "-MsixPath", msixPath,
                "-ExpectedAppId", ExpectedAppId,
                "-AllowUnsigned");
            psExit.Should().Be(0, "install-msix.ps1 must install + verify + remove the MSIX");
        }
        finally
        {
            try { Directory.Delete(outDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    private static async Task<int> RunDotnetAsync(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        await proc.WaitForExitAsync();
        return proc.ExitCode;
    }

    private static async Task<int> RunPowerShellAsync(string script, params string[] args)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(script);
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        await proc.WaitForExitAsync();
        return proc.ExitCode;
    }
}
