using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SigilBuild.Packaging.Msix;

public sealed class MakePriRunner
{
    private readonly string _exePath;

    public MakePriRunner(string exePath) { _exePath = exePath; }

    public static MakePriRunner FromSdk()
    {
        if (!WindowsSdkLocator.TryLocateBin(out var bin))
            throw new FileNotFoundException("Windows SDK not found; cannot locate makepri.exe");
        return new MakePriRunner(Path.Combine(bin, "makepri.exe"));
    }

    public async Task<int> CreateConfigAsync(string output, CancellationToken ct)
    {
        return await RunAsync(new[] { "createconfig", "/cf", output, "/dq", "en-US", "/o" }, ct);
    }

    public async Task<int> NewAsync(string projectRoot, string configPath, string outputDir, CancellationToken ct)
    {
        return await RunAsync(new[] { "new", "/pr", projectRoot, "/cf", configPath, "/of", outputDir, "/o" }, ct);
    }

    private async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_exePath) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        await proc.WaitForExitAsync(ct);
        return proc.ExitCode;
    }
}
