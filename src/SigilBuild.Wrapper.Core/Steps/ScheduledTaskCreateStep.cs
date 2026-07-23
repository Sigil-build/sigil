namespace SigilBuild.Wrapper.Steps;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;

/// <summary>
/// P11 (T11.1) machine-scope-only <c>scheduled_task_create</c> step. Creates a
/// Windows Scheduled Task via <c>schtasks.exe /Create</c>, running the task as
/// <c>SYSTEM</c> — which is why the step overrides
/// <see cref="InstallStep.RequiresMachineScope"/> to <c>true</c> (see
/// <see cref="SigilBuild.Core.Configuration.MachineScopeGuard"/> / SIG0310).
/// Records a <see cref="RollbackRecord.DeleteScheduledTask"/> BEFORE the create
/// so a mid-install crash and <c>setup.exe /Uninstall</c> both unwind the task —
/// mirrors <see cref="ServiceInstallStep"/>'s <c>RemoveService</c> pattern.
/// </summary>
/// <remarks>
/// <para>
/// Uses schtasks.exe (shipped on every supported Windows SKU) rather than the
/// Task Scheduler COM API — the COM surface pulls interop marshalling into the
/// AOT graph for no benefit over a well-understood CLI. Each argument is passed
/// via <see cref="ProcessStartInfo.ArgumentList"/> so the runtime handles
/// quoting; the composed <c>/TR</c> value additionally quotes the program path
/// itself (schtasks parses <c>/TR</c>'s value as its own mini command line, so
/// a spaced path needs its own quotes even though the whole value already
/// arrives as one <see cref="ProcessStartInfo.ArgumentList"/> entry).
/// </para>
/// <para>
/// <b>DAILY determinism:</b> <c>schtasks /SC DAILY</c> requires a start time
/// (<c>/ST</c>). Using the current wall-clock time would make two pack runs of
/// the identical manifest schtasks-create at different times and would leak the
/// packing machine's clock into the installed task — a nondeterminism this
/// project avoids elsewhere (e.g. reproducible zip/MSIX timestamps). Midnight
/// (<c>00:00</c>) is used as a fixed, documented default; the task still fires
/// daily, just at a predictable, author-documentable time. <c>logon</c> and
/// <c>onstart</c> triggers need no start time.
/// </para>
/// <para>
/// <c>/F</c> forces overwrite so a repeat run (reinstall / repair) is
/// idempotent, mirroring the idempotency other steps aim for. Non-zero exit is
/// surfaced via <see cref="StepResult.Failed(string)"/> with schtasks.exe's
/// stderr (falling back to stdout) so operators can diagnose without
/// re-running under a debugger.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class ScheduledTaskCreateStep : IStep
{
    private readonly InstallStep.ScheduledTaskCreate _spec;

    public ScheduledTaskCreateStep(InstallStep.ScheduledTaskCreate spec)
    {
        _spec = spec;
    }

    public async Task<StepResult> RunAsync(StepContext ctx, RollbackJournal journal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(journal);

        var name = ctx.Resolve(_spec.Name);
        // Program may reference the extracted payload (payload://) or {install_dir}.
        var program = ctx.ResolvePath(_spec.Program);
        var arguments = _spec.Arguments is null ? null : ctx.Resolve(_spec.Arguments);

        if (string.IsNullOrWhiteSpace(name))
        {
            return StepResult.Failed("scheduled_task_create: name is empty after substitution");
        }
        if (string.IsNullOrWhiteSpace(program))
        {
            return StepResult.Failed("scheduled_task_create: program is empty after substitution");
        }

        // Record the inverse BEFORE the create so an interrupted install still
        // tears the task down on /Uninstall. Only the task name is journaled —
        // no secrets, no resolved program path.
        journal.Append(new RollbackRecord.DeleteScheduledTask(name));

        var args = BuildCreateArgs(name, program, arguments, _spec.Trigger, _spec.RunLevel);
        var result = await RunSchtasksAsync(args, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            return StepResult.Failed(
                $"scheduled_task_create '{name}': schtasks.exe /Create exited {result.ExitCode}. " +
                $"Output: {(string.IsNullOrEmpty(result.Stderr) ? result.Stdout : result.Stderr)}");
        }

        return StepResult.Ok();
    }

    /// <summary>
    /// Builds the <c>schtasks.exe /Create</c> argument list from already-resolved
    /// values. A pure, side-effect-free seam so the exact argument construction —
    /// including the DAILY <c>/ST</c> determinism default — is unit-testable
    /// without executing schtasks.exe or requiring admin rights. The live
    /// create+query+delete leg (which needs <c>/RU SYSTEM</c> elevation) is
    /// verified on the CI VM (see AGENTS.md §2).
    /// </summary>
    internal static List<string> BuildCreateArgs(
        string name, string program, string? arguments, string trigger, string runLevel)
    {
        // schtasks parses /TR's value as its own command line, so the program
        // part is quoted even though the whole value is one ArgumentList entry.
        var trValue = string.IsNullOrEmpty(arguments)
            ? $"\"{program}\""
            : $"\"{program}\" {arguments}";

        var args = new List<string> { "/Create", "/TN", name, "/TR", trValue, "/SC", MapTrigger(trigger) };

        if (string.Equals(trigger, "daily", StringComparison.Ordinal))
        {
            // See the DAILY-determinism remark on the type: a fixed, documented
            // start time avoids baking the packing machine's clock into the task.
            args.Add("/ST");
            args.Add("00:00");
        }

        args.Add("/RL");
        args.Add(MapRunLevel(runLevel));
        args.Add("/RU");
        args.Add("SYSTEM"); // machine-scope only: SYSTEM needs no password.
        args.Add("/F");     // force overwrite: reinstall/repair is idempotent.
        return args;
    }

    private static string MapTrigger(string trigger) => trigger switch
    {
        "logon"   => "ONLOGON",
        "daily"   => "DAILY",
        "onstart" => "ONSTART",
        _         => "ONLOGON",
    };

    private static string MapRunLevel(string runLevel) => runLevel switch
    {
        "highest" => "HIGHEST",
        _         => "LIMITED",
    };

    private readonly record struct SchtasksResult(int ExitCode, string Stdout, string Stderr);

    private static async Task<SchtasksResult> RunSchtasksAsync(IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Process.Start returned null for schtasks.exe");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        return new SchtasksResult(proc.ExitCode,
            (await stdoutTask.ConfigureAwait(false)).TrimEnd(),
            (await stderrTask.ConfigureAwait(false)).TrimEnd());
    }
}
