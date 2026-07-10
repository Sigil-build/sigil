namespace SigilBuild.Wrapper.Steps;

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;

/// <summary>
/// MUST-tier <c>run_program</c> step. Spawns an external executable with the
/// configured arguments / cwd / timeout and (optionally) waits for it to
/// exit, asserting the exit code lies in the manifest-supplied
/// <c>expected_exit_codes</c> set.
///
/// Deliberately records NO journal entry: <c>run_program</c> has no inverse
/// (the engine cannot un-run a side-effect-having binary). When this step
/// fails with <c>on_failure: rollback</c>, the engine walks the journal
/// backwards and undoes prior steps, which is the correct cascade.
///
/// Uses <see cref="ProcessStartInfo.ArgumentList"/> so each argument is
/// quoted by the runtime — avoids the classic CommandLine quoting bugs.
/// stdout / stderr are redirected so they are silently consumed; future
/// tasks can wire them into the audit log.
/// </summary>
internal sealed class RunProgramStep : IStep
{
    private readonly InstallStep.RunProgram _spec;

    public RunProgramStep(InstallStep.RunProgram spec)
    {
        _spec = spec;
    }

    public async Task<StepResult> RunAsync(StepContext ctx, RollbackJournal journal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        // Program / working dir may reference the extracted payload (payload://).
        var program = ctx.ResolvePath(_spec.Program);
        var psi = new ProcessStartInfo
        {
            FileName = program,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = _spec.Cwd is null ? string.Empty : ctx.ResolvePath(_spec.Cwd),
        };
        if (_spec.Args is not null)
        {
            foreach (var a in _spec.Args)
            {
                psi.ArgumentList.Add(ctx.Resolve(a));
            }
        }

        Process? proc;
        try
        {
            proc = Process.Start(psi);
        }
#pragma warning disable CA1031 // Step boundary: surface any spawn failure as a typed StepResult.
        catch (Exception ex)
        {
            return StepResult.Failed($"'{program}' failed to start: {ex.Message}");
        }
#pragma warning restore CA1031

        if (proc is null)
        {
            return StepResult.Failed($"could not start '{program}'");
        }

        // run_program has no inverse: NO journal entry is recorded. Engine-level
        // rollback covers prior steps if this one fails.
        using (proc)
        {
            if (!_spec.Wait)
            {
                // Fire-and-forget: handle is disposed but the OS process keeps running.
                return StepResult.Ok();
            }

            // Drain stdout/stderr concurrently so the pipe buffer never fills
            // (4 KB on Windows). Capturing both gives us forensic context to
            // include in any failure message — without this, the engine surfaces
            // only the exit code, which is rarely enough to diagnose a real
            // failure (e.g. "sc.exe exit 5" with no hint of the SCM error).
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);

            using var timeoutCts = _spec.TimeoutSeconds is null
                ? null
                : CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (timeoutCts is not null && _spec.TimeoutSeconds is int timeoutSeconds)
            {
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            }

            var waitToken = timeoutCts?.Token ?? ct;
            try
            {
                await proc.WaitForExitAsync(waitToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
#pragma warning disable CA1031 // Best-effort kill — the process may already have died.
                try { proc.Kill(entireProcessTree: true); }
                catch { /* best-effort */ }
#pragma warning restore CA1031
                return StepResult.Failed(
                    $"'{program}' timed out after {_spec.TimeoutSeconds}s");
            }

            // Await both drains so we don't lose the tail of output if the
            // child closed its pipes right before exit.
            var stdout = (await stdoutTask.ConfigureAwait(false)).TrimEnd();
            var stderr = (await stderrTask.ConfigureAwait(false)).TrimEnd();

            var expected = _spec.ExpectedExitCodes ?? new[] { 0 };
            if (!expected.Contains(proc.ExitCode))
            {
                var argSummary = _spec.Args is null ? "" : " " + string.Join(' ', _spec.Args.Select(a => '"' + a + '"'));
                var detail = string.IsNullOrEmpty(stderr) ? stdout : stderr;
                if (string.IsNullOrEmpty(detail)) detail = "(no output)";
                return StepResult.Failed(
                    $"'{program}'{argSummary} exited {proc.ExitCode}; expected one of [{string.Join(", ", expected)}]. Output: {detail}");
            }

            return StepResult.Ok();
        }
    }
}
