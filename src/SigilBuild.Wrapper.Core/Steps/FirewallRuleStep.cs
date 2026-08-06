namespace SigilBuild.Wrapper.Steps;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;

/// <summary>
/// P11 (T11.3) machine-scope-only <c>firewall_rule</c> step — third and last
/// of the three P11 "system steps". Creates a Windows Defender Firewall rule
/// via <c>netsh advfirewall firewall add rule</c>, which is why the step
/// overrides <see cref="InstallStep.RequiresMachineScope"/> to <c>true</c>
/// (see <see cref="SigilBuild.Core.Configuration.MachineScopeGuard"/> /
/// SIG0310). Records a <see cref="RollbackRecord.DeleteFirewallRule"/> BEFORE
/// the add so a mid-install crash and <c>setup.exe /Uninstall</c> both unwind
/// the rule — mirrors <see cref="ServiceInstallStep"/>'s <c>RemoveService</c>
/// pattern (exec a shipped Windows CLI, journal the inverse before the
/// mutation).
/// </summary>
/// <remarks>
/// <para>
/// Uses netsh.exe (shipped on every supported Windows SKU) rather than the
/// COM-based <c>INetFwPolicy2</c> firewall API — same rationale as
/// <c>ScheduledTaskCreateStep</c> preferring schtasks.exe over the Task
/// Scheduler COM surface: a well-understood CLI avoids pulling COM interop
/// into the AOT graph. Each <c>key=value</c> pair is passed as ONE
/// <see cref="ProcessStartInfo.ArgumentList"/> token (no space around the
/// <c>=</c>) — netsh parses <c>name=Foo</c> as a single argument, and
/// splitting it into two tokens or adding a space would break parsing.
/// </para>
/// <para>
/// <b>Reinstall idempotency:</b> unlike <c>sc create</c> (no equivalent
/// overwrite flag) or <c>schtasks /Create /F</c>, a repeated
/// <c>netsh advfirewall firewall add rule</c> with a duplicate
/// <c>name=</c> ADDS a second rule — netsh explicitly allows same-named
/// rules. To keep a reinstall/repair idempotent, this step deletes any
/// existing rule with the same name (best-effort, tolerating "no rules
/// match") immediately before the add, rather than accepting duplicate-name
/// growth across repeated installs.
/// </para>
/// <para>
/// Non-zero exit from the add is surfaced via
/// <see cref="StepResult.Failed(string)"/> with netsh's stderr (falling back
/// to stdout) so operators can diagnose without re-running under a debugger.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class FirewallRuleStep : IStep
{
    private readonly InstallStep.FirewallRule _spec;

    public FirewallRuleStep(InstallStep.FirewallRule spec)
    {
        _spec = spec;
    }

    public async Task<StepResult> RunAsync(StepContext ctx, RollbackJournal journal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(journal);

        var name = ctx.Resolve(_spec.Name);
        // Program may reference the extracted payload (payload://) or {install_dir}.
        var program = _spec.Program is null ? null : ctx.ResolvePath(_spec.Program);

        if (string.IsNullOrWhiteSpace(name))
        {
            return StepResult.Failed("firewall_rule: name is empty after substitution");
        }

        // R3/R9: `program=` scopes the rule to one executable, so a path an
        // unprivileged user can replace hands that user the firewall exemption the
        // publisher granted their own binary. Only checked when a program was
        // declared — a program-less rule has no target to anchor. Before the
        // journal entry, so a refused step never queues a DeleteFirewallRule for a
        // rule this installer did not add.
        if (program is not null)
        {
            var refusal = PrivilegedTargetGuard.Check("firewall_rule", "program", ctx.InstallDir, program);
            if (refusal is not null)
            {
                return StepResult.Failed(refusal);
            }
        }

        // Record the inverse BEFORE any mutation (including the idempotency
        // pre-delete below) so an interrupted install still tears the rule
        // down on /Uninstall. Only the rule name is journaled — no secrets,
        // no resolved program path.
        journal.Append(new RollbackRecord.DeleteFirewallRule(name));

        // Reinstall idempotency: delete any pre-existing rule of this name
        // first (best-effort — a fresh install simply has nothing to delete;
        // "no rules match the specified criteria" is not treated as failure)
        // so a repeat pack/install run doesn't accumulate duplicate rules.
        await RunNetshAsync(BuildDeleteArgs(name), ct).ConfigureAwait(false);

        var addArgs = BuildAddArgs(name, _spec.Direction, _spec.Action, program, _spec.Port, _spec.Protocol);
        var result = await RunNetshAsync(addArgs, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            return StepResult.Failed(
                $"firewall_rule '{name}': netsh advfirewall firewall add rule exited {result.ExitCode}. " +
                $"Output: {(string.IsNullOrEmpty(result.Stderr) ? result.Stdout : result.Stderr)}");
        }

        return StepResult.Ok();
    }

    /// <summary>
    /// Builds the <c>netsh advfirewall firewall add rule</c> argument list
    /// from already-resolved values. A pure, side-effect-free seam so the
    /// exact argument construction is unit-testable without executing
    /// netsh.exe or requiring admin rights. The live
    /// add → show rule → reverse leg is verified on the CI VM (see
    /// AGENTS.md §2).
    /// </summary>
    internal static List<string> BuildAddArgs(
        string name, string direction, string action, string? program, int? port, string? protocol)
    {
        var args = new List<string>
        {
            "advfirewall", "firewall", "add", "rule",
            $"name={name}",
            $"dir={direction}",
            $"action={action}",
        };

        if (!string.IsNullOrEmpty(program))
        {
            args.Add($"program={program}");
        }
        if (port is not null)
        {
            args.Add($"localport={port.Value.ToString(CultureInfo.InvariantCulture)}");
        }
        if (!string.IsNullOrEmpty(protocol))
        {
            args.Add($"protocol={protocol}");
        }
        args.Add("enable=yes");
        return args;
    }

    /// <summary>
    /// Builds the <c>netsh advfirewall firewall delete rule</c> argument
    /// list, targeting the rule by name only. Used both by the idempotency
    /// pre-delete in <see cref="RunAsync"/> and by
    /// <see cref="RollbackRecord.DeleteFirewallRule"/>'s undo.
    /// </summary>
    internal static List<string> BuildDeleteArgs(string name) =>
        new() { "advfirewall", "firewall", "delete", "rule", $"name={name}" };

    private readonly record struct NetshResult(int ExitCode, string Stdout, string Stderr);

    private static async Task<NetshResult> RunNetshAsync(IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("netsh.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Process.Start returned null for netsh.exe");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        return new NetshResult(proc.ExitCode,
            (await stdoutTask.ConfigureAwait(false)).TrimEnd(),
            (await stderrTask.ConfigureAwait(false)).TrimEnd());
    }
}
