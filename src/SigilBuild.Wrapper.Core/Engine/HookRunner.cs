namespace SigilBuild.Wrapper.Engine;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Steps;

/// <summary>
/// Outcome of running one lifecycle-hook phase (P2). <see cref="Success"/> is
/// false only when a hook step with <c>on_failure: fail</c> failed; the offending
/// step id / error are carried for the caller's abort message + log.
/// </summary>
internal readonly record struct HookOutcome(bool Success, string? FailedStepId, string? Error)
{
    public static HookOutcome Ok() => new(true, null, null);
    public static HookOutcome Failed(string id, string? error) => new(false, id, error);
}

/// <summary>
/// Runs a lifecycle-hook phase (P2, gap G2) — an ordered list of ordinary step
/// records — <em>outside</em> the rollback journal. Honors each step's
/// <c>when</c> and <c>on_failure</c>:
/// <list type="bullet">
///   <item><description><see cref="OnFailure.Continue"/> — log the failure and go on.</description></item>
///   <item><description><see cref="OnFailure.Fail"/> / <see cref="OnFailure.Rollback"/> — abort the
///   phase (there is no journal to roll back, so <c>rollback</c> is treated as
///   <c>fail</c>).</description></item>
/// </list>
/// A throwaway journal is handed to each step to satisfy <see cref="IStep"/>, but
/// it is never replayed: hooks have <b>no rollback obligations</b> (loudly
/// documented in the schema + docs). Hook lines are reported as message-only
/// <see cref="StepProgress"/> (<c>Total = 0</c>) so they land in the /LOG file and
/// the wizard log pane without moving the progress bar.
/// </summary>
internal static class HookRunner
{
    public static async Task<HookOutcome> RunAsync(
        string phaseLabel,
        IReadOnlyList<InstallStep>? hooks,
        StepContext ctx,
        IProgress<StepProgress>? progress,
        CancellationToken ct)
    {
        if (hooks is null || hooks.Count == 0)
        {
            return HookOutcome.Ok();
        }

        // Never replayed — hooks get no rollback (P2). Present only because IStep
        // takes a journal; run_program (the common hook) records nothing anyway.
        var discard = new RollbackJournal();

        try
        {
            foreach (var spec in hooks)
            {
                ct.ThrowIfCancellationRequested();
                if (spec.When is not null && !ctx.Evaluate(spec.When))
                {
                    continue;
                }

                StepResult result;
                try
                {
                    var step = StepFactory.Create(spec);
                    result = await step.RunAsync(ctx, discard, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
#pragma warning disable CA1031 // Hook boundary: surface any step exception as a typed failure.
                catch (Exception ex)
                {
                    result = new StepResult(false, ex.Message);
                }
#pragma warning restore CA1031

                if (result.Success)
                {
                    Report(progress, ctx, $"hook {phaseLabel}: {Describe(spec)}", isError: false);
                    continue;
                }

                var msg = $"hook {phaseLabel}: step '{spec.Id}' failed: {result.Error}";
                if (spec.OnFailure == OnFailure.Continue)
                {
                    Report(progress, ctx, msg + " (continue)", isError: true);
                    continue;
                }

                // fail / rollback → abort the phase (no journal to unwind).
                Report(progress, ctx, msg, isError: true);
                return HookOutcome.Failed(spec.Id, result.Error);
            }

            return HookOutcome.Ok();
        }
        finally
        {
            // A post_install / post_uninstall hook phase runs on the SAME StepContext
            // after InstallEngine's own finally has already released this run's
            // {staging_dir}. A hook resolving the token there used to create a second
            // SecureStaging that nobody owned and nothing ever disposed — a hardened
            // directory leaked per install, in %ProgramData% on an elevated run. This
            // gives that directory the same phase-bounded lifetime the install body's has.
            // A no-op for a phase that resolved no token, and a no-op for a pre_install
            // phase, which runs BEFORE the engine and whose staging the engine still owns.
            ctx.ReleasePostRunStaging();
        }
    }

    private static void Report(IProgress<StepProgress>? progress, StepContext ctx, string message, bool isError)
        => progress?.Report(new StepProgress(0, 0, ctx.Redact(message), isError));

    // Declared (unresolved) fields only, so a resolved secret can never leak.
    private static string Describe(InstallStep spec) => spec switch
    {
        InstallStep.RunProgram rp => $"run {rp.Program}",
        InstallStep.FileCopy fc => $"copy {fc.From} → {fc.To}",
        InstallStep.DirectoryCreate dc => $"mkdir {dc.Path}",
        _ => spec.Id,
    };
}
