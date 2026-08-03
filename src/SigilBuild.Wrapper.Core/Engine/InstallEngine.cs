using System;
using System.Collections.Generic;
using System.Linq;
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
        return RunAsync(Array.Empty<InstallStep>(), steps, Array.Empty<InstallStep>(), ctx, progress: null, ct: ct);
    }

    /// <summary>
    /// Run a full <c>pre_install</c> → <c>install_steps</c> → <c>post_install</c>
    /// sequence under a single shared <see cref="RollbackJournal"/>. When
    /// <paramref name="progress"/> is supplied the engine emits a
    /// <see cref="StepProgress"/> per executed (or skipped) step plus
    /// <c>error:</c>/<c>rollback:</c> lines on failure, so the GUI host and the
    /// headless console can render identical logs from one engine run.
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
        IProgress<StepProgress>? progress = null,
        CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(preInstall);
        System.ArgumentNullException.ThrowIfNull(installSteps);
        System.ArgumentNullException.ThrowIfNull(postInstall);
        System.ArgumentNullException.ThrowIfNull(ctx);

        var installList = installSteps as IReadOnlyList<InstallStep> ?? installSteps.ToList();
        var reporter = progress is null
            ? null
            : new ProgressReporter(progress, preInstall.Count + installList.Count + postInstall.Count);

        // P4: let a long-running step (http_download) stream intra-step rows to the
        // same progress channel (wizard + /LOG) without moving the overall bar.
        ctx.ProgressSink = progress;

        var journal = new RollbackJournal();
        try
        {
            await ExecutePhaseAsync(preInstall, ctx, journal, phaseLabel: "pre_install", reporter, ct).ConfigureAwait(false);
            await ExecutePhaseAsync(installList, ctx, journal, phaseLabel: "install_steps", reporter, ct).ConfigureAwait(false);
            await ExecutePhaseAsync(postInstall, ctx, journal, phaseLabel: "post_install", reporter, ct).ConfigureAwait(false);

            return EngineResult.Ok(journal);
        }
        catch (StepFailureException ex)
        {
            // Redact in case a step surfaced a resolved secret in its error text.
            // ex.Message already names the failing step ("step '<id>' failed: …").
            reporter?.ReportMessage($"error: {ctx.Redact(ex.Message)}", isError: true);
            reporter?.ReportMessage("rollback: reverting changes", isError: true);
            // Stream each reversal (P7): passing `progress` makes UndoAsync emit a
            // per-record line (delete / rmdir / path - / reg -) so the rollback
            // trail lands in the /LOG file and the wizard log pane, not just the
            // summary line above.
            // InProcess (R1): every record here was authored moments ago by the loop
            // above, from the signed manifest. Nothing has round-tripped through a file
            // an attacker can write, and anchoring it would refuse legitimate reversals
            // of manifest-declared work outside the install directory.
            await journal.UndoAsync(ReplayAnchorage.InProcess, progress, ct).ConfigureAwait(false);
            return EngineResult.Failed(journal, ex.Message);
        }
        catch (System.OperationCanceledException)
        {
            reporter?.ReportMessage("rollback: reverting changes", isError: true);
            await journal.UndoAsync(ReplayAnchorage.InProcess, progress, CancellationToken.None)
                .ConfigureAwait(false);
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
        ProgressReporter? reporter,
        CancellationToken ct)
    {
        foreach (var spec in steps)
        {
            ct.ThrowIfCancellationRequested();
            if (spec.When is not null && !ctx.Evaluate(spec.When))
            {
                // Skipped: advance the fraction but emit no log line.
                reporter?.Advance(message: null, isError: false);
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
                reporter?.Advance(ctx.Redact(Describe(spec)), isError: false);
                continue;
            }

            switch (spec.OnFailure)
            {
                case OnFailure.Continue:
                    // Non-fatal: advance the fraction, keep going.
                    reporter?.Advance(ctx.Redact(Describe(spec)), isError: false);
                    continue;
                case OnFailure.Rollback:
                case OnFailure.Fail:
                default:
                    throw new StepFailureException(spec.Id, $"{phaseLabel}: {result.Error}");
            }
        }
    }

    /// <summary>
    /// Render a short, prototype-style log line for a step from its declared
    /// (unresolved) fields. Deliberately never resolves parameter templates, so
    /// secret values can never leak into the log.
    /// </summary>
    private static string Describe(InstallStep spec) => spec switch
    {
        InstallStep.FileCopy fc => $"copy {fc.From} → {fc.To}",
        InstallStep.DirectoryCreate dc => $"mkdir {dc.Path}",
        InstallStep.FileDelete fd => $"del {fd.Path}",
        InstallStep.DirectoryDelete dd => $"rmdir {dd.Path}",
        InstallStep.RegistryWrite rw => $"reg {rw.Hive}\\{rw.Key}\\{rw.Name}",
        InstallStep.RegistryDeleteValue rdv => $"reg del {rdv.Hive}\\{rdv.Key}\\{rdv.Name}",
        InstallStep.RegistryDeleteKey rdk => $"reg del {rdk.Hive}\\{rdk.Key}",
        InstallStep.ShortcutCreate sc => $"link {sc.Location}\\{sc.Name}",
        InstallStep.EnvSet es => es.Name.Equals("PATH", StringComparison.OrdinalIgnoreCase)
            ? $"path + {es.Value}"
            : $"env {es.Name}={es.Value}",
        InstallStep.RunProgram rp => $"run {rp.Program}",
        InstallStep.HttpDownload hd => $"download {hd.Url} → {hd.Dest}",
        InstallStep.IniWrite iw => $"ini {iw.Path} [{iw.Section}] {iw.Key}",
        InstallStep.JsonEdit je => $"json {je.Path} {je.JsonPointer}",
        InstallStep.XmlEdit xe => $"xml {xe.Path} {xe.Xpath}",
        _ => spec.Id,
    };

    /// <summary>
    /// Mutable progress cursor shared across the three phases: tracks how many
    /// steps have completed against the fixed total and forwards each event to
    /// the caller-supplied <see cref="IProgress{T}"/>.
    /// </summary>
    private sealed class ProgressReporter
    {
        private readonly IProgress<StepProgress> _sink;
        private readonly int _total;
        private int _completed;

        public ProgressReporter(IProgress<StepProgress> sink, int total)
        {
            _sink = sink;
            _total = total;
        }

        /// <summary>Advance the completed counter by one and report (message may be null for a silent advance).</summary>
        public void Advance(string? message, bool isError)
        {
            _completed++;
            _sink.Report(new StepProgress(_completed, _total, message, isError));
        }

        /// <summary>Report a log line without advancing the counter (error / rollback lines).</summary>
        public void ReportMessage(string message, bool isError)
            => _sink.Report(new StepProgress(_completed, _total, message, isError));
    }
}
