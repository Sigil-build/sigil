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
public sealed class InstallEngine
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "Public engine surface is intentionally instance-based: future tasks attach logging, metrics, and pre/post hook plumbing to the instance.")]
    public async Task<EngineResult> RunAsync(
        IEnumerable<InstallStep> steps,
        StepContext ctx,
        CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(steps);
        System.ArgumentNullException.ThrowIfNull(ctx);

        var journal = new RollbackJournal();
        try
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
                        throw new StepFailureException(spec.Id, result.Error);
                }
            }
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
}
