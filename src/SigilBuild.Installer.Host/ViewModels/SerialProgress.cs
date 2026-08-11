namespace SigilBuild.Installer.Host.ViewModels;

using System;
using System.Threading;

/// <summary>
/// An <see cref="IProgress{T}"/> that delivers every report to <b>one</b> consumer at a
/// time, in the order the reports were made.
/// </summary>
/// <remarks>
/// <para>
/// The BCL's <see cref="Progress{T}"/> captures <see cref="SynchronizationContext.Current"/>
/// at construction and, when there is none, posts each <c>Report</c> to the <b>thread pool</b>.
/// Under Avalonia that is harmless — the wizard builds its progress sink on the UI thread, so
/// a context exists and every callback is serialised onto it. Anywhere without a
/// synchronisation context (the headless view-model tests, and any future non-UI host) each
/// report lands on an arbitrary pool thread, so two callbacks that the engine made
/// sequentially can execute concurrently. The handlers here mutate an
/// <see cref="System.Collections.ObjectModel.ObservableCollection{T}"/> (<c>LogLines</c>),
/// which is not thread-safe: two racing <c>Add</c> calls can interleave inside the backing
/// list and lose an item. That is the mechanism behind the intermittent
/// <c>InstallFlowTests</c> / <c>UninstallFlowTests</c> log-line failures — measured at
/// roughly 4 runs in 20 — a pre-existing defect in the progress plumbing, not in the tests.
/// </para>
/// <para>
/// This type keeps the exact production behaviour where a context exists (post to it, so the
/// handler still runs on the UI thread and never blocks the engine), and replaces the
/// thread-pool fan-out with a synchronous, lock-serialised invocation where it does not. The
/// handler therefore always observes a single-threaded, ordered stream of reports.
/// </para>
/// </remarks>
internal sealed class SerialProgress<T> : IProgress<T>
{
    private readonly Action<T> _handler;
    private readonly SynchronizationContext? _context;
    private readonly object _gate = new();

    public SerialProgress(Action<T> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handler = handler;
        _context = SynchronizationContext.Current;
    }

    public void Report(T value)
    {
        if (_context is not null)
        {
            // Same contract as Progress<T> under Avalonia: marshal to the captured
            // context, which is itself a single ordered consumer (the UI thread).
            _context.Post(_ => _handler(value), null);
            return;
        }

        // No context: run inline under a lock rather than fanning out to the thread
        // pool, so concurrent reporters cannot interleave inside the handler.
        lock (_gate)
        {
            _handler(value);
        }
    }
}
