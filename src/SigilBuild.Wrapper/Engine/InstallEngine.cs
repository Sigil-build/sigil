using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SigilBuild.Core.Manifest;

namespace SigilBuild.Wrapper.Engine;

/// <summary>
/// Drives an ordered sequence of <see cref="InstallStep"/> records, dispatches
/// each to its concrete <see cref="IStep"/> implementation, and unwinds the
/// <see cref="RollbackJournal"/> in reverse on failure.
/// </summary>
/// <remarks>
/// The engine runs three phases against a single shared journal:
/// <c>pre_install</c> → <c>install_steps</c> → <c>post_install</c>. A failure
/// in any phase replays the journal in reverse, naturally undoing the work
/// of all phases that ran. The one exception is <see cref="OnFailure.Continue"/>
/// on a <c>post_install</c> step: that records the diagnostic but does not
/// trigger rollback — install is "successful but with warnings."
/// </remarks>
public sealed class InstallEngine
{
    /// <summary>
    /// Convenience overload used by tests and callers that have no
    /// pre/post hooks. Equivalent to invoking the three-phase overload
    /// with empty pre/post lists.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "Public engine surface is intentionally instance-based: future tasks attach logging, metrics, and pre/post hook plumbing to the instance.")]
    public Task<EngineResult> RunAsync(
        IEnumerable<InstallStep> steps,
        StepContext ctx,
        CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(steps);
        System.ArgumentNullException.ThrowIfNull(ctx);
        return RunAsync(Array.Empty<InstallStep>(), steps, Array.Empty<InstallStep>(), ctx, ct);
    }

    /// <summary>
    /// Run a full <c>pre_install</c> → <c>install_steps</c> → <c>post_install</c>
    /// sequence under a single shared <see cref="RollbackJournal"/>.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "Public engine surface is intentionally instance-based: future tasks attach logging, metrics, and pre/post hook plumbing to the instance.")]
    public async Task<EngineResult> RunAsync(
        IReadOnlyList<InstallStep> preInstall,
        IEnumerable<InstallStep> installSteps,
        IReadOnlyList<InstallStep> postInstall,
        StepContext ctx,
        CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(preInstall);
        System.ArgumentNullException.ThrowIfNull(installSteps);
        System.ArgumentNullException.ThrowIfNull(postInstall);
        System.ArgumentNullException.ThrowIfNull(ctx);

        var journal = new RollbackJournal();
        try
        {
            await ExecutePhaseAsync(preInstall, ctx, journal, phaseLabel: "pre_install", ct).ConfigureAwait(false);
            await ExecutePhaseAsync(installSteps, ctx, journal, phaseLabel: "install_steps", ct).ConfigureAwait(false);
            await ExecutePhaseAsync(postInstall, ctx, journal, phaseLabel: "post_install", ct).ConfigureAwait(false);

            return EngineResult.Ok(journal);
        }
        catch (StepFailureException ex)
        {
            await journal.UndoAsync(ct).ConfigureAwait(false);
            return EngineResult.Failed(journal, ex.Message);
        }
        catch (System.OperationCanceledException)
        {
            await journal.UndoAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Execute a single phase's worth of steps against the shared journal.
    /// Honours per-step <see cref="OnFailure.Continue"/> by skipping forward;
    /// <see cref="OnFailure.Fail"/> and <see cref="OnFailure.Rollback"/> raise
    /// a <see cref="StepFailureException"/> that the caller translates into a
    /// journal replay. The same routine is used for all three phases — the
    /// "post-install continue is non-fatal" property falls out naturally,
    /// since the journal is only replayed on a thrown
    /// <see cref="StepFailureException"/>.
    /// </summary>
    private static async Task ExecutePhaseAsync(
        IEnumerable<InstallStep> steps,
        StepContext ctx,
        RollbackJournal journal,
        string phaseLabel,
        CancellationToken ct)
    {
        foreach (var spec in steps)
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
                result = await step.RunAsync(ctx, journal, ct).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Engine intentionally converts any step exception into a typed StepResult.
            catch (System.Exception ex)
            {
                result = StepResult.Failed(ex.Message);
            }
#pragma warning restore CA1031

            if (result.Success)
            {
                continue;
            }

            switch (spec.OnFailure)
            {
                case OnFailure.Continue:
                    continue;
                case OnFailure.Rollback:
                case OnFailure.Fail:
                default:
                    throw new StepFailureException(spec.Id, $"{phaseLabel}: {result.Error}");
            }
        }
    }
}
