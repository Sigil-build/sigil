namespace SigilBuild.Wrapper.Engine;

using System;
using System.Threading;
using System.Threading.Tasks;
using SigilBuild.Core.Manifest;
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
    /// Drive the auto-derived uninstall flow for <paramref name="appId"/> in the
    /// scope it was installed under (T12). <paramref name="preferredScope"/> is the
    /// scope resolved from the uninstall command line (the ARP
    /// <c>UninstallString</c> carries <c>/allusers</c> or <c>/currentuser</c>); the
    /// state store searches it first, then the recorded scope in the state file
    /// drives ARP-hive and state-dir selection so the uninstall reverses exactly
    /// what the install wrote.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "Public engine surface is intentionally instance-based, mirroring InstallEngine.")]
    public async Task<EngineResult> RunAsync(
        string appId, InstallScope preferredScope = InstallScope.User, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(appId);

        var loaded = UninstallStateStore.TryLoad(appId, preferredScope);
        if (loaded is null)
        {
            return EngineResult.Failed(
                new RollbackJournal(),
                $"no uninstall state found for '{appId}' (expected at {UninstallStateStore.PathFor(appId, preferredScope)})");
        }

        await loaded.Journal.UndoAsync(ct).ConfigureAwait(false);

        // Remove the ARP entry we wrote on install, from the recorded scope's hive.
        // Best-effort: if the user already cleaned it manually, keep going.
        if (OperatingSystem.IsWindows())
        {
            ArpRegistration.Remove(appId, loaded.Scope);
        }

        UninstallStateStore.Delete(appId, loaded.Scope);
        return EngineResult.Ok(loaded.Journal);
    }
}
