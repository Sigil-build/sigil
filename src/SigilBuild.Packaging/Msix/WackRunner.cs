using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SigilBuild.Packaging.Msix;

public sealed record WackResult(int ExitCode, string ReportPath);

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage(Justification = "Wraps the Windows App Cert Kit (appcert.exe); exercised only in Windows integration tests.")]
public sealed class WackRunner
{
    private readonly string _exePath;

    public WackRunner(string exePath) { _exePath = exePath; }

    public static bool TryFromInstalled(out WackRunner runner)
    {
        var paths = new[]
        {
            @"C:\Program Files (x86)\Windows Kits\10\App Certification Kit\appcert.exe",
            @"C:\Program Files\Windows Kits\10\App Certification Kit\appcert.exe",
        };
        foreach (var p in paths)
        {
            if (File.Exists(p)) { runner = new WackRunner(p); return true; }
        }
        runner = default!;
        return false;
    }

    public async Task<WackResult> RunAsync(string msixPath, string reportXmlPath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_exePath) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "test", "-appxpackagepath", msixPath, "-reportoutputpath", reportXmlPath })
            psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        await proc.WaitForExitAsync(ct);
        return new WackResult(proc.ExitCode, reportXmlPath);
    }
}
