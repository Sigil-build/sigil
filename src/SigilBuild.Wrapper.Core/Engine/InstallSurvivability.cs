namespace SigilBuild.Wrapper.Engine;

using System;
using System.IO;
using SigilBuild.Core.Manifest;

/// <summary>
/// The final install step that makes uninstall survive deletion of the downloaded
/// setup exe (spec T15, decision 8): copy the running installer image into the
/// install directory as <c>uninstall.exe</c>, and journal its removal so the
/// uninstall (and a rollback) reverse it. ARP's <c>UninstallString</c> then points
/// at this copy — never at <see cref="Environment.ProcessPath"/> (the original
/// download, which breaks the moment the user deletes it).
/// </summary>
public static class InstallSurvivability
{
    /// <summary>The file name the running installer is copied to inside the install dir.</summary>
    public const string UninstallerFileName = "uninstall.exe";

    /// <summary>
    /// Copy <paramref name="sourceExe"/> to <c>{installDir}\uninstall.exe</c> and
    /// append a <see cref="RollbackRecord.RemoveUninstaller"/> to
    /// <paramref name="journal"/> so the file is removed on uninstall (and on a
    /// rollback). The removal record is appended <em>before</em> the copy, mirroring
    /// <c>FileCopyStep</c>, so a crash mid-write still leaves a correct undo entry.
    /// Returns the full path of the copied uninstaller (for the ARP
    /// <c>UninstallString</c>).
    /// </summary>
    /// <remarks>
    /// The copy is skipped when the source already resolves to the destination, so
    /// this never truncates the running image (e.g. a re-entrant call).
    /// </remarks>
    public static string CopyUninstaller(RollbackJournal journal, string sourceExe, string installDir)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentException.ThrowIfNullOrEmpty(sourceExe);
        ArgumentException.ThrowIfNullOrEmpty(installDir);

        Directory.CreateDirectory(installDir);
        var dest = Path.Combine(installDir, UninstallerFileName);

        // Journal the removal BEFORE the copy — uninstall/rollback replays this in
        // reverse; the record tolerates the running image (see SelfDelete).
        journal.Append(new RollbackRecord.RemoveUninstaller(dest));

        if (!string.Equals(
                Path.GetFullPath(sourceExe),
                Path.GetFullPath(dest),
                StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(sourceExe, dest, overwrite: true);
        }

        return dest;
    }

    /// <summary>
    /// Copy the running installer image (<see cref="Environment.ProcessPath"/>) into
    /// the resolved install directory as <c>uninstall.exe</c> and journal its
    /// removal. Returns the copied uninstaller path, or <c>null</c> when the running
    /// image path is unavailable (nothing to copy).
    /// </summary>
    /// <remarks>
    /// T15 destination seam: the install root is T12's per-scope
    /// <see cref="ScopeLayout.InstallRoot"/> plus <paramref name="appId"/>. T13 owns
    /// <c>{install_dir}</c> resolution and will feed the resolved install directory
    /// here once it lands; until then the ScopeLayout install root is the stable,
    /// available base (per the spec T15 note). Only the AppId-scoped subdirectory is
    /// used so two apps sharing a scope root never clobber each other's uninstaller.
    /// </remarks>
    public static string? InstallUninstaller(RollbackJournal journal, InstallScope scope, string appId)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentException.ThrowIfNullOrEmpty(appId);

        var source = Environment.ProcessPath;
        if (string.IsNullOrEmpty(source))
        {
            return null;
        }

        // TODO(T13): replace with the resolved {install_dir} once T13 threads it
        // through StepContext; the ScopeLayout root + AppId is the T15 stand-in.
        var installDir = Path.Combine(ScopeLayout.For(scope).InstallRoot, appId);
        return CopyUninstaller(journal, source, installDir);
    }
}
