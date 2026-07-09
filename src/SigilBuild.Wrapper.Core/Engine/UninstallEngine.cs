namespace SigilBuild.Wrapper.Engine;

using System;
using System.Threading;
using System.Threading.Tasks;
using SigilBuild.Wrapper.Cli;

/// <summary>
/// Replays a persisted <see cref="RollbackJournal"/> in reverse to undo a
/// previous successful install. Reads state from <c>UninstallStateStore</c>,
/// drives <see cref="RollbackJournal.UndoAsync"/>, removes the ARP entry,
/// and finally cleans up the per-app state directory.
/// </summary>
/// <remarks>
/// Missing or corrupt state is a documented degradation: Task 19 reports
/// the gap explicitly rather than fabricating a best-effort uninstall.
/// </remarks>
public sealed class UninstallEngine
{
    /// <summary>
    /// Drive the auto-derived uninstall flow for <paramref name="appId"/>.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "Public engine surface is intentionally instance-based, mirroring InstallEngine.")]
    public async Task<EngineResult> RunAsync(string appId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(appId);

        var journal = UninstallStateStore.TryLoad(appId);
        if (journal is null)
        {
            return EngineResult.Failed(
                new RollbackJournal(),
                $"no uninstall state found for '{appId}' (expected at {UninstallStateStore.PathFor(appId)})");
        }

        await journal.UndoAsync(ct).ConfigureAwait(false);

        // Remove the ARP entry we wrote on install. Best-effort: if the user
        // already cleaned it manually, keep going.
        if (OperatingSystem.IsWindows())
        {
            ArpRegistration.Remove(appId);
        }

        UninstallStateStore.Delete(appId);
        return EngineResult.Ok(journal);
    }
}
