// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

namespace SigilBuild.Wrapper.Engine;

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

/// <summary>
/// What <see cref="SelfDelete.Remove(string)"/> did with the target file.
/// </summary>
public enum SelfDeleteOutcome
{
    /// <summary>The file was already gone; nothing to do.</summary>
    NotPresent,

    /// <summary>The file was deleted immediately (it was not the running image).</summary>
    Deleted,

    /// <summary>
    /// The file could not be deleted now (it is the running <c>uninstall.exe</c>
    /// image, or otherwise locked), and could not be moved aside either. Deletion was
    /// scheduled for the next reboot via
    /// <c>MoveFileExW(target, NULL, MOVEFILE_DELAY_UNTIL_REBOOT)</c>.
    /// </summary>
    ScheduledForReboot,

    /// <summary>
    /// The file could not be deleted now — the original <c>uninstall.exe</c> is still
    /// the image of the process waiting on its temporary copy — so it was RENAMED into
    /// that copy's private directory instead, which a running image permits. The install
    /// directory no longer holds it; the relaunch parent cleans up the private directory.
    /// (R106)
    /// </summary>
    SetAside,
}

/// <summary>
/// Deletes a file that may be the currently-running executable image — the
/// self-deletion problem for the survivable <c>uninstall.exe</c>. A running
/// Windows process holds an exclusive lock on its own image, so <c>uninstall.exe</c>
/// cannot delete itself while it runs. The mechanism: if the target is <em>not</em> the
/// running image, delete it directly; if it cannot be deleted and a set-aside directory
/// is configured, rename it there; otherwise schedule removal at the next reboot via
/// <c>MoveFileExW(path, NULL, MOVEFILE_DELAY_UNTIL_REBOOT)</c>.
/// </summary>
/// <remarks>
/// <para>
/// The set-aside branch is what keeps the install directory empty after an uninstall.
/// The uninstaller in the install directory hands its run to a copy of itself in a
/// private temporary directory (<see cref="UninstallerRelaunch"/>) and waits on it, so
/// when the copy replays the journal the original is still a mapped image and cannot be
/// deleted — but a mapped image CAN be renamed, within one volume. Moving it into the
/// copy's directory takes it out of the install directory at once, so the journal's
/// <c>remove_directory</c> records find the directory empty and remove it. (R106)
/// </para>
/// <para>
/// All interop is source-generated <c>[LibraryImport]</c> (Native-AOT safe): no
/// reflection, no COM. The reboot scheduling is best-effort — an unelevated
/// per-user uninstall lacks the rights to write <c>PendingFileRenameOperations</c>
/// under HKLM, in which case the file stays where it is until a manual cleanup. A
/// leftover file is preferable to a crash on uninstall.
/// </para>
/// </remarks>
public static partial class SelfDelete
{
    // winbase.h: delete the file at the next boot when lpNewFileName is NULL.
    private const uint MOVEFILE_DELAY_UNTIL_REBOOT = 0x00000004;

    /// <summary>
    /// The directory a file that cannot be deleted is renamed into instead, or
    /// <c>null</c> when this process is not the temporary copy of a relaunched
    /// uninstaller. Set once, by <see cref="UninstallerRelaunch.ConfigureRelaunchedCopy"/>,
    /// only after the original it was relaunched from has been verified. (R106)
    /// </summary>
    internal static string? SetAsideDirectory { get; set; }

    /// <summary>
    /// Remove <paramref name="targetPath"/>, tolerating the case where it is this
    /// process's own image (or the image of the relaunch parent waiting on it). Never
    /// throws — a failure to schedule the delete is swallowed so journal replay can call
    /// this on the uninstaller's own entry without aborting the rest of the uninstall.
    /// </summary>
    public static SelfDeleteOutcome Remove(string targetPath) =>
        Remove(targetPath, Environment.ProcessPath, SetAsideDirectory);

    /// <summary>
    /// Test seam: <paramref name="runningImagePath"/> is normally
    /// <see cref="Environment.ProcessPath"/> but is injectable so a unit test can
    /// exercise the "target is the running image" branch against a temp file
    /// without operating on the real test-host binary.
    /// </summary>
    internal static SelfDeleteOutcome Remove(string targetPath, string? runningImagePath) =>
        Remove(targetPath, runningImagePath, setAsideDirectory: null);

    /// <summary>
    /// Test seam: <see cref="Remove(string, string?)"/> with the set-aside directory
    /// injected rather than read from <see cref="SetAsideDirectory"/>. (R106)
    /// </summary>
    internal static SelfDeleteOutcome Remove(
        string targetPath, string? runningImagePath, string? setAsideDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetPath);

        if (!File.Exists(targetPath))
        {
            return SelfDeleteOutcome.NotPresent;
        }

        var isRunningImage = runningImagePath is not null &&
            string.Equals(
                Path.GetFullPath(targetPath),
                Path.GetFullPath(runningImagePath),
                StringComparison.OrdinalIgnoreCase);

        if (!isRunningImage)
        {
#pragma warning disable CA1031 // A locked file falls through to set-aside / reboot scheduling; do not surface the IO error.
            try
            {
                File.Delete(targetPath);
                return SelfDeleteOutcome.Deleted;
            }
            catch (Exception)
            {
                // Locked by something else — typically the relaunch parent, whose image
                // this is. Treat like the running image below.
            }
#pragma warning restore CA1031
        }

        if (!OperatingSystem.IsWindows())
        {
            return SelfDeleteOutcome.ScheduledForReboot;
        }

        // Out of the install directory now, when there is somewhere private to put it.
        // Never this process's own image: the copy's image already lives in the set-aside
        // directory, and moving it would gain nothing. (R106)
        if (!isRunningImage
            && !string.IsNullOrEmpty(setAsideDirectory)
            && TrySetAside(targetPath, setAsideDirectory))
        {
            return SelfDeleteOutcome.SetAside;
        }

        // Cannot delete the live image now: schedule it for the next reboot.
        _ = ScheduleDeleteOnReboot(targetPath);
        return SelfDeleteOutcome.ScheduledForReboot;
    }

    /// <summary>
    /// Rename <paramref name="targetPath"/> into <paramref name="setAsideDirectory"/>
    /// under a fresh name. A plain rename only — <c>MOVEFILE_COPY_ALLOWED</c> is NOT
    /// passed, because the copy-then-delete it permits across volumes would leave the
    /// original in place (it cannot be deleted) and a second copy beside it. A different
    /// volume therefore falls back to reboot scheduling at the original path. (R106)
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static bool TrySetAside(string targetPath, string setAsideDirectory)
    {
#pragma warning disable CA1031 // Best-effort: any failure falls back to reboot scheduling.
        try
        {
            var full = Path.GetFullPath(targetPath);
            var aside = Path.GetFullPath(setAsideDirectory);
            if (!Directory.Exists(aside)
                || string.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(full) ?? string.Empty),
                    Path.TrimEndingDirectorySeparator(aside),
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var destination = Path.Combine(aside, $"original-{Guid.NewGuid():N}.exe");
            return MoveFileEx(full, destination, 0);
        }
        catch (Exception)
        {
            return false;
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Ask Windows to delete <paramref name="path"/> (a file, or an EMPTY directory) at
    /// the next boot. Pending deletes run in the order they were recorded, so a caller
    /// schedules a directory's files before the directory. Best-effort: <c>false</c>
    /// (never a throw) when the caller lacks rights to record the pending operation,
    /// which is every unelevated process. (R106)
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static bool ScheduleDeleteOnReboot(string path) =>
        MoveFileEx(path, null, MOVEFILE_DELAY_UNTIL_REBOOT);

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveFileEx(string lpExistingFileName, string? lpNewFileName, uint dwFlags);
}
