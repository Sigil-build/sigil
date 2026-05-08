using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SigilBuild.Packaging.Msix;

namespace SigilBuild.Signing.Local;

public sealed record SignToolResult(int ExitCode, string StdOut, string StdErr);

public sealed class SignToolRunner
{
    private readonly string _exePath;

    public SignToolRunner(string exePath) { _exePath = exePath; }

    public static SignToolRunner FromSdk()
    {
        if (!WindowsSdkLocator.TryLocateBin(out var bin))
            throw new FileNotFoundException("Windows SDK not found; cannot locate signtool.exe");
        return new SignToolRunner(Path.Combine(bin, "signtool.exe"));
    }

    public async Task<SignToolResult> SignWithPfxAsync(
        string artifactPath, string pfxPath, string? pfxPassword,
        string timestampUrl, CancellationToken ct)
    {
        var args = new List<string>
        {
            "sign", "/fd", "SHA256",
            "/f", pfxPath,
            "/tr", timestampUrl, "/td", "SHA256",
        };
        if (!string.IsNullOrEmpty(pfxPassword))
        {
            args.Add("/p"); args.Add(pfxPassword);
        }
        args.Add(artifactPath);
        return await RunAsync(args, ct);
    }

    private async Task<SignToolResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_exePath)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        try
        {
            proc.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new SignToolResult(-1, string.Empty, $"Failed to launch '{_exePath}': {ex.Message}");
        }
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        return new SignToolResult(proc.ExitCode, await stdoutTask, await stderrTask);
    }
}
