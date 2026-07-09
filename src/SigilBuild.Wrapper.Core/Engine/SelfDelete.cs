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
    /// image, or otherwise locked). Deletion was scheduled for the next reboot via
    /// <c>MoveFileExW(target, NULL, MOVEFILE_DELAY_UNTIL_REBOOT)</c> — the v1
    /// self-deletion fallback (spec T15).
    /// </summary>
    ScheduledForReboot,
}

/// <summary>
/// Deletes a file that may be the currently-running executable image — the
/// self-deletion problem for the survivable <c>uninstall.exe</c> (spec T15,
/// decision 8). A running Windows process holds an exclusive lock on its own
/// image, so <c>uninstall.exe</c> cannot delete itself while it runs. The v1
/// mechanism (per the spec): if the target is <em>not</em> the running image,
/// delete it directly; if it <em>is</em>, schedule removal at the next reboot via
/// <c>MoveFileExW(path, NULL, MOVEFILE_DELAY_UNTIL_REBOOT)</c>.
/// </summary>
/// <remarks>
/// All interop is source-generated <c>[LibraryImport]</c> (Native-AOT safe): no
/// reflection, no COM. The reboot scheduling is best-effort — an unelevated
/// per-user uninstall may lack rights to write <c>PendingFileRenameOperations</c>
/// under HKLM, in which case the <c>uninstall.exe</c> simply lingers in the
/// (otherwise emptied) install directory until a manual cleanup. A leftover file
/// is preferable to a crash on uninstall, and a payload-stripped stub plus a
/// %TEMP% relaunch are documented follow-up optimizations.
/// </remarks>
public static partial class SelfDelete
{
    // winbase.h: delete the file at the next boot when lpNewFileName is NULL.
    private const uint MOVEFILE_DELAY_UNTIL_REBOOT = 0x00000004;

    /// <summary>
    /// Remove <paramref name="targetPath"/>, tolerating the case where it is this
    /// process's own image. Never throws — a failure to schedule the delete is
    /// swallowed so journal replay can call this on the uninstaller's own entry
    /// without aborting the rest of the uninstall.
    /// </summary>
    public static SelfDeleteOutcome Remove(string targetPath) =>
        Remove(targetPath, Environment.ProcessPath);

    /// <summary>
    /// Test seam: <paramref name="runningImagePath"/> is normally
    /// <see cref="Environment.ProcessPath"/> but is injectable so a unit test can
    /// exercise the "target is the running image" branch against a temp file
    /// without operating on the real test-host binary.
    /// </summary>
    internal static SelfDeleteOutcome Remove(string targetPath, string? runningImagePath)
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
#pragma warning disable CA1031 // A locked file falls through to reboot scheduling; do not surface the IO error.
            try
            {
                File.Delete(targetPath);
                return SelfDeleteOutcome.Deleted;
            }
            catch (Exception)
            {
                // Locked by something else — treat like the running image below.
            }
#pragma warning restore CA1031
        }

        // Cannot delete the live image now: schedule it for the next reboot.
        if (OperatingSystem.IsWindows())
        {
            ScheduleDeleteOnReboot(targetPath);
        }
        return SelfDeleteOutcome.ScheduledForReboot;
    }

    [SupportedOSPlatform("windows")]
    private static void ScheduleDeleteOnReboot(string path)
    {
        // Best-effort: MoveFileExW returns false (never throws) if the caller lacks
        // rights to record the pending rename. The bool is intentionally discarded.
        _ = MoveFileEx(path, null, MOVEFILE_DELAY_UNTIL_REBOOT);
    }

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveFileEx(string lpExistingFileName, string? lpNewFileName, uint dwFlags);
}
