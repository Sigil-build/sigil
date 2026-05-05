using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SigilBuild.Packaging.Msix;

public sealed record MakeAppxResult(int ExitCode, string StdOut, string StdErr);

public sealed class MakeAppxRunner
{
    private readonly string _exePath;

    public MakeAppxRunner(string exePath) { _exePath = exePath; }

    public static MakeAppxRunner FromSdk()
    {
        if (!WindowsSdkLocator.TryLocateBin(out var bin))
            throw new FileNotFoundException("Windows SDK not found; cannot locate MakeAppx.exe");
        return new MakeAppxRunner(Path.Combine(bin, "MakeAppx.exe"));
    }

    public async Task<MakeAppxResult> PackAsync(string contentDirectory, string outputPath, CancellationToken ct)
    {
        var args = new List<string> { "pack", "/d", contentDirectory, "/p", outputPath, "/o" };
        return await RunAsync(args, ct);
    }

    private async Task<MakeAppxResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_exePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        return new MakeAppxResult(proc.ExitCode, await stdoutTask, await stderrTask);
    }
}
