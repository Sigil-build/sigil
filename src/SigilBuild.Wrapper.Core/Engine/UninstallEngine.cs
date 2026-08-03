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
    /// state store reads that scope's directory and only that one, and the scope of
    /// the directory the state was found in drives ARP-hive and state-dir selection
    /// (R1 — never the <c>scope</c> field inside the file).
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "Public engine surface is intentionally instance-based, mirroring InstallEngine.")]
    public async Task<EngineResult> RunAsync(
        string appId,
        InstallScope preferredScope = InstallScope.User,
        IProgress<StepProgress>? progress = null,
        string? installDir = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(appId);

        // progress is threaded through so an R1 state refusal reaches the console,
        // the wizard log pane and the /LOG file instead of vanishing.
        var attempt = UninstallStateStore.Load(appId, preferredScope, progress);

        // R1: a refusal is NOT an absence. Reporting "no uninstall state found" here
        // would tell the operator the opposite of what happened — the brief's exact
        // "reads as no prior install" failure mode — and would hide the attack from
        // the one line an incident responder reads.
        if (attempt.RefusalReason is not null)
        {
            return EngineResult.Failed(
                new RollbackJournal(),
                $"uninstall state for '{appId}' was found but REFUSED, not replayed: " +
                $"{attempt.RefusalReason}. Nothing was uninstalled. Remove the directory as " +
                "an administrator and reinstall, or investigate who created it.");
        }

        var loaded = attempt.State;
        if (loaded is null)
        {
            return EngineResult.Failed(
                new RollbackJournal(),
                $"no uninstall state found for '{appId}' (expected at {UninstallStateStore.PathFor(appId, preferredScope)})");
        }

        // R1 clause (c): this journal came off disk. Anchor the replay to the install
        // directory the caller resolved from the signed blob / command line, so a
        // planted record cannot aim the elevated process at System32, HKLM\SYSTEM, a
        // service the app never installed, or a DLL of the attacker's choosing.
        var undo = await loaded.Journal
            .UndoAsync(ct, progress, installDir)
            .ConfigureAwait(false);

        if (undo.RefusedRecords.Count > 0)
        {
            // Never silent: a refusal here is either a planted journal or an anchoring
            // bug, and both need a human. S5 surfaces this per-record for R15.
            progress?.Report(new StepProgress(
                0,
                0,
                $"{undo.RefusedRecords.Count} rollback record(s) were REFUSED and not " +
                "replayed because their target lies outside this installation — see the " +
                "lines above; the uninstall continued with the remaining records",
                IsError: true));
        }

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
