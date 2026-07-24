namespace SigilBuild.Wrapper.IntegrationTests;

using System.Diagnostics;
using System.Threading.Tasks;

/// <summary>
/// Tiny process-runner shared by the P11 system-step VM integration tests
/// (<c>scheduled_task_create</c> / <c>firewall_rule</c>): both verify the live
/// effect of their step by shelling out to the same OS query tools an operator
/// would use (<c>schtasks.exe /Query</c>, <c>netsh advfirewall firewall show
/// rule</c>). Captures stdout/stderr so assertions can inspect the tool's own
/// text output rather than guessing at exit-code semantics that vary across
/// Windows builds for "no match" cases.
/// </summary>
internal static class SystemStepProcessRunner
{
    public readonly record struct Result(int ExitCode, string Stdout, string Stderr);

    public static async Task<Result> RunAsync(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = Process.Start(psi)
            ?? throw new System.InvalidOperationException($"Process.Start returned null for {exe}");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync().ConfigureAwait(false);
        return new Result(
            proc.ExitCode,
            (await stdoutTask.ConfigureAwait(false)).TrimEnd(),
            (await stderrTask.ConfigureAwait(false)).TrimEnd());
    }

    /// <summary>Best-effort cleanup — swallow all failures, same tolerance as
    /// the production rollback records for these two steps.</summary>
    public static async Task BestEffortAsync(string exe, params string[] args)
    {
#pragma warning disable CA1031 // best-effort test cleanup
        try
        {
            await RunAsync(exe, args).ConfigureAwait(false);
        }
        catch
        {
            // best-effort
        }
#pragma warning restore CA1031
    }
}
