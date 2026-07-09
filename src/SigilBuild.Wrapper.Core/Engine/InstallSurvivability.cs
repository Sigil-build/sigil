namespace SigilBuild.Wrapper.Engine;

using System;
using System.IO;

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
    /// <paramref name="installDir"/> as <c>uninstall.exe</c> and journal its removal.
    /// Returns the copied uninstaller path, or <c>null</c> when the running image
    /// path is unavailable (nothing to copy).
    /// </summary>
    /// <remarks>
    /// T13 destination seam (now wired): <paramref name="installDir"/> is the SINGLE
    /// resolved install directory that <see cref="InstallDirResolver"/> computed for
    /// this run (honoring <c>/D=</c>, the manifest <c>install_dir</c>, the
    /// wizard-collected path, else <c>&lt;scope root&gt;\&lt;App.Name&gt;</c>) and
    /// that the install steps copied files into. The uninstaller lands in the exact
    /// same directory, so ARP's <c>UninstallString</c> can never diverge from where
    /// the files actually landed. The directory is created if a step did not already
    /// (see <see cref="CopyUninstaller"/>).
    /// </remarks>
    public static string? InstallUninstaller(RollbackJournal journal, string installDir)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentException.ThrowIfNullOrEmpty(installDir);

        var source = Environment.ProcessPath;
        if (string.IsNullOrEmpty(source))
        {
            return null;
        }

        return CopyUninstaller(journal, source, installDir);
    }
}
