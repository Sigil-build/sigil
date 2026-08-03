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
    /// <param name="fallbackInstallDir">
    /// The install directory resolved for the CURRENT run (manifest / <c>/D=</c> /
    /// default). Required and non-optional so this entry point — the only one that
    /// replays persisted state — cannot be called without an anchor. It is only a
    /// fallback: the replay anchors to the directory RECORDED in the state file, which
    /// is the one the install actually used. A recomputed default silently refuses
    /// every file record of any install that used a wizard-chosen or <c>/D=</c>
    /// destination, because the ARP <c>UninstallString</c> carries no <c>/D=</c>.
    /// </param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "Public engine surface is intentionally instance-based, mirroring InstallEngine.")]
    public async Task<EngineResult> RunAsync(
        string appId,
        string fallbackInstallDir,
        InstallScope preferredScope = InstallScope.User,
        IProgress<StepProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(appId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackInstallDir);

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

        // R1 clause (c): this journal came off disk, so anchor the replay — a planted
        // record must not be able to aim the elevated process at System32, HKLM\SYSTEM,
        // a service the app never installed, or a DLL of the attacker's choosing.
        //
        // Anchor to the RECORDED install dir, not to a recomputed default. The default
        // is wrong for every install that used /D= or a wizard-chosen destination (the
        // ARP UninstallString carries no /D=), and anchoring to it would refuse all of
        // that install's file records while still removing the ARP row and the state —
        // leaving the app unremovable with its files on disk.
        var anchorDir = ChooseAnchorDirectory(
            appId, loaded.Scope, loaded.InstallDir, fallbackInstallDir, progress);

        var undo = await loaded.Journal
            .UndoAsync(ReplayAnchorage.ForInstallDir(anchorDir), progress, ct)
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

    /// <summary>
    /// Directories that can never be an install directory, and so can never be accepted
    /// as an anchor. Anchoring to any of them would make the anchor vacuous — every
    /// record under it would pass.
    /// </summary>
    private static string[] ForbiddenAnchors() => new[]
    {
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
    };

    /// <summary>
    /// Pick the directory the replay is anchored to, in descending order of how
    /// certainly it is where the app is actually installed:
    /// <list type="number">
    ///   <item>the directory RECORDED in the state file at install time;</item>
    ///   <item>the ARP <c>InstallLocation</c> for this app in its own scope's hive;</item>
    ///   <item>the directory the caller resolved for the current run.</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Step 2 exists for the installed base. State written before the recorded field
    /// existed has no (1), and (3) is a RECOMPUTED DEFAULT — right only for installs
    /// that took the default destination, because the ARP <c>UninstallString</c> carries
    /// no <c>/D=</c>. Anchoring a <c>/D=</c> or wizard-chosen install to that default
    /// refuses every one of its file records while the ARP row and the state are removed
    /// anyway, which leaves the app on disk and unremovable. The ARP
    /// <c>InstallLocation</c> is written by every install since P3 and is the one place
    /// the real directory survives; for machine scope it lives in HKLM and is therefore
    /// admin-authored.
    /// </para>
    /// <para>
    /// Both (1) and (2) are read from data an install wrote, so both pass the sanity
    /// floor first — it rejects a value chosen to make the anchor meaningless, such as
    /// <c>C:\</c>, <c>%WINDIR%</c> or <c>%ProgramFiles%</c> itself. It is a floor, not a
    /// whitelist: any real install directory, including a <c>/D=</c> path anywhere on
    /// the volume, passes it. (3) is trusted input from this process and is used as-is.
    /// </para>
    /// </remarks>
    private static string ChooseAnchorDirectory(
        string appId,
        InstallScope scope,
        string? recorded,
        string fallback,
        IProgress<StepProgress>? progress)
    {
        if (!string.IsNullOrWhiteSpace(recorded))
        {
            if (IsPlausibleInstallDirectory(recorded))
            {
                return recorded;
            }

            progress?.Report(new StepProgress(
                0,
                0,
                $"the recorded install directory '{recorded}' is not a directory any install " +
                "could have used; falling back to the ARP InstallLocation or this run's " +
                "resolved directory instead",
                IsError: true));
        }

        // Pre-fix state (or a rejected recorded value): recover the real directory from
        // the ARP row this app wrote at install time.
        if (OperatingSystem.IsWindows())
        {
            var arp = ArpRegistration.TryGetInstallLocation(appId, scope);
            if (!string.IsNullOrWhiteSpace(arp) && IsPlausibleInstallDirectory(arp))
            {
                return arp;
            }
        }

        return fallback;
    }

    private static bool IsPlausibleInstallDirectory(string candidate)
    {
#pragma warning disable CA1031 // Fail closed: an unparseable value is never an acceptable anchor.
        try
        {
            var full = System.IO.Path.TrimEndingDirectorySeparator(
                System.IO.Path.GetFullPath(candidate));

            // A volume root anchors nothing.
            var root = System.IO.Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root) ||
                full.Equals(System.IO.Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            foreach (var forbidden in ForbiddenAnchors())
            {
                if (string.IsNullOrEmpty(forbidden))
                {
                    continue;
                }
                var normalized = System.IO.Path.TrimEndingDirectorySeparator(
                    System.IO.Path.GetFullPath(forbidden));
                if (full.Equals(normalized, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            return true;
        }
        catch
        {
            return false;
        }
#pragma warning restore CA1031
    }
}
