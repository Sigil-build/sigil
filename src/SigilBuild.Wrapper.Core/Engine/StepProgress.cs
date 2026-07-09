namespace SigilBuild.Wrapper.Engine;

/// <summary>
/// A single progress report emitted by <see cref="InstallEngine"/> as it walks
/// the step pipeline. Consumed by the GUI host (to grow the on-screen log and
/// advance the progress bar) and by the headless console path (to echo the log
/// to stdout). Both entry points share one engine, so both share this shape.
/// </summary>
/// <param name="Completed">Number of pipeline steps finished (0-based advances to <paramref name="Total"/>).</param>
/// <param name="Total">Total steps across the <c>pre_install → install → post_install</c> phases.</param>
/// <param name="Message">
/// Human-readable log line for this event (e.g. <c>copy … → …</c>, <c>reg …</c>,
/// <c>path + …</c>, <c>link …</c>, or an <c>error:</c> / <c>rollback:</c> line),
/// or <c>null</c> for a fraction-only advance (a skipped <c>when:</c>-gated step).
/// Derived from the step's declared (unresolved) fields, so parameter values —
/// including secrets — never appear here.
/// </param>
/// <param name="IsError">True for <c>error:</c> / <c>rollback:</c> lines that should render in the danger colour.</param>
public sealed record StepProgress(int Completed, int Total, string? Message, bool IsError)
{
    /// <summary>Fraction complete in <c>[0, 1]</c>; <c>1.0</c> when the pipeline is empty.</summary>
    public double Fraction =>
        Total <= 0 ? 1.0 : System.Math.Clamp((double)Completed / Total, 0.0, 1.0);
}
