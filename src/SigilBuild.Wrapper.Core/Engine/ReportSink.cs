namespace SigilBuild.Wrapper.Engine;

using System;

/// <summary>
/// Adapts the engine's <c>(message, isError)</c> callback shape onto
/// <see cref="IProgress{T}"/> of <see cref="StepProgress"/>, so a component that reports
/// through the former can pass a sink to one that takes the latter.
/// </summary>
/// <remarks>
/// <para>
/// It exists for exactly one reason: <c>StateDirectorySecurity.CreateHardened</c> takes an
/// optional <see cref="IProgress{T}"/> and uses it to announce that it <b>took ownership
/// of an existing directory</b> — a repair that changes who controls a machine-scope path.
/// Three call sites in this assembly were passing nothing, so that line went nowhere.
/// A silently-repaired ACL is the same failure shape as a silently-disarmed check: it
/// reads, afterwards, exactly like a directory that was fine all along.
/// </para>
/// <para>
/// Synchronous on purpose. <see cref="Progress{T}"/> posts to the captured
/// synchronization context, which would reorder these lines relative to the surrounding
/// install log — and for a diagnostic whose entire value is "this happened at this point
/// in the sequence", arriving later is close to not arriving.
/// </para>
/// </remarks>
internal sealed class ReportSink : IProgress<StepProgress>
{
    private readonly Action<string, bool> _report;

    private ReportSink(Action<string, bool> report) => _report = report;

    /// <summary>
    /// A sink forwarding to <paramref name="report"/>, or <c>null</c> when there is
    /// nothing to forward to — so a caller with no sink passes <c>null</c> rather than
    /// wrapping a discard, which would look like a decision nobody made.
    /// </summary>
    public static ReportSink? For(Action<string, bool>? report) =>
        report is null ? null : new ReportSink(report);

    public void Report(StepProgress value)
    {
        if (value?.Message is { } message)
        {
            _report(message, value.IsError);
        }
    }
}
