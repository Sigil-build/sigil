// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

namespace SigilBuild.Wrapper.Engine;

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;

/// <summary>
/// Removes the private directories that earlier uninstalls left behind
/// (<c>sigil-uninstall-&lt;32 hex&gt;</c>). Run once at the start of every setup and
/// uninstall run. Best-effort: never throws, and every failure means "leave it alone".
/// (R114)
/// </summary>
/// <remarks>
/// <para>
/// <b>The leak.</b> <see cref="UninstallerRelaunch"/> runs the uninstall from a copy in a
/// fresh staging directory, and the waiting parent's own image is renamed into it. A
/// running image cannot be deleted, so the parent schedules the directory for deletion
/// at the next restart — which needs administrator rights. An unelevated (per-user)
/// uninstall therefore leaves one directory in <c>%TEMP%</c> each time. The uninstaller
/// is never elevated to fix that: a per-user app's uninstall must not raise UAC. The
/// next Sigil run cleans up instead.
/// </para>
/// <para>
/// <b>Only the uninstall prefix.</b> It is the one staging directory that outlives its
/// run by design. The others (<c>prereq</c>, <c>stage</c>, <c>hook</c>, <c>update</c>) are
/// removed by the run that made them and are left only by a crash or a kill, and their
/// contents can be in use by a concurrent install for longer than any age threshold — a
/// staged download that is waiting to be verified and run holds no handle that would stop
/// a sweep. An uninstall directory holds only images that are mapped while they run.
/// </para>
/// <para>
/// <b>What is removed.</b> A directory directly under a swept root whose name is exactly
/// <c>sigil-uninstall-</c> plus 32 lower-case hex digits (<see cref="IsSweepableName"/>),
/// and only when all of these hold, each read from a handle that does not follow a
/// reparse point (<see cref="DirectoryHandle"/>):
/// </para>
/// <list type="bullet">
///   <item><description>it is a plain directory, not a junction or symbolic link;</description></item>
///   <item><description>under <c>%ProgramData%</c>, it is administrator-only writable, so no
///   non-administrator can have planted it or can swap it before the delete;</description></item>
///   <item><description>it is at least <see cref="MinimumAge"/> old, so an uninstall that is
///   copying and verifying its uninstaller right now is not raced;</description></item>
///   <item><description>it is not the directory this process runs from;</description></item>
///   <item><description>it holds only plain files — a subdirectory or a reparse point of any
///   kind leaves the whole directory alone;</description></item>
///   <item><description>every file in it can be opened exclusively at the same time, so
///   nothing is in use and no image is mapped by a live process. All-or-nothing: the probe
///   finishes before a single file is deleted, and one busy file leaves the directory
///   untouched. There is no retry.</description></item>
/// </list>
/// <para>
/// <b>Where.</b> Unelevated, the current user's <c>%TEMP%</c>. Elevated, <c>%ProgramData%</c>
/// only — where an elevated uninstall stages — and never <c>%TEMP%</c>: an elevated
/// process's <c>%TEMP%</c> is writable by the same user's unelevated processes, and a
/// delete there with administrator rights is the planted-link file-delete primitive. The
/// directories an unelevated uninstall leaves are swept by the next unelevated run, and
/// a per-machine setup's first, unelevated process is one.
/// </para>
/// <para>
/// <b>Why the delete itself may be by path.</b> Under <c>%ProgramData%</c> the
/// administrator-only check licenses it, as in the native-runtime reclaim (R50):
/// <c>BUILTIN\Users</c> may create children there but not delete or rename them, and the
/// directory's protected DACL denies them write. Under an unelevated <c>%TEMP%</c> there
/// is no privilege boundary to cross: everything that could swap a path there runs as
/// the same user as this process.
/// </para>
/// </remarks>
public static class StaleStagingSweep
{
    /// <summary>The name prefix <see cref="SecureStaging"/> gives an uninstaller's copy directory.</summary>
    internal const string Prefix = "sigil-" + UninstallerRelaunch.StagingPurpose + "-";

    /// <summary>How old a directory must be before it is considered abandoned.</summary>
    internal static readonly TimeSpan MinimumAge = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Sweep the roots that apply to this process. Never throws; every removal and every
    /// failure is reported on <paramref name="log"/>. (R114)
    /// </summary>
    public static void Run(Action<string, bool> log)
    {
        ArgumentNullException.ThrowIfNull(log);
#pragma warning disable CA1031 // Housekeeping must never fail a setup or an uninstall run.
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            var ownDirectory = OwnImageDirectory();
            if (Elevation.IsProcessElevated())
            {
                var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                if (!string.IsNullOrEmpty(programData))
                {
                    Sweep(programData, requireAdminOnly: true, DateTime.UtcNow, MinimumAge, ownDirectory, log);
                }
            }
            else
            {
                Sweep(Path.GetTempPath(), requireAdminOnly: false, DateTime.UtcNow, MinimumAge, ownDirectory, log);
            }
        }
        catch (Exception ex)
        {
            Report(log, $"temporary files: the sweep for leftover uninstaller copies failed ({ex.Message})", false);
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Sweep <paramref name="root"/>: remove each abandoned uninstaller copy directory
    /// directly under it and return how many were removed. Never throws. The test seam
    /// for <see cref="Run"/>, with the root, the clock and the age threshold injected. (R114)
    /// </summary>
    internal static int Sweep(
        string root,
        bool requireAdminOnly,
        DateTime utcNow,
        TimeSpan minimumAge,
        string? ownDirectory,
        Action<string, bool> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(root))
        {
            return 0;
        }

        string[] candidates;
#pragma warning disable CA1031 // An unreadable root is a no-op sweep, never a failed run.
        try
        {
            if (!Directory.Exists(root))
            {
                return 0;
            }
            candidates = Directory.GetDirectories(root, Prefix + "*");
        }
        catch (Exception ex)
        {
            Report(log, $"temporary files: could not list '{root}' ({ex.Message})", false);
            return 0;
        }
#pragma warning restore CA1031

        var removed = 0;
        foreach (var candidate in candidates)
        {
            if (!IsSweepableName(Path.GetFileName(candidate)))
            {
                continue;
            }
            if (ownDirectory is not null && UninstallerRelaunch.IsWithin(ownDirectory, candidate))
            {
                continue;
            }
            if (TryRemove(candidate, requireAdminOnly, utcNow, minimumAge, log))
            {
                removed++;
            }
        }
        return removed;
    }

    /// <summary>
    /// Exactly <c>sigil-uninstall-</c> followed by 32 lower-case hex digits — the shape
    /// <see cref="SecureStaging"/> creates with <c>Guid.NewGuid():N</c>, and nothing else.
    /// </summary>
    internal static bool IsSweepableName(string? name)
    {
        if (name is null
            || name.Length != Prefix.Length + 32
            || !name.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        for (var i = Prefix.Length; i < name.Length; i++)
        {
            var c = name[i];
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
            {
                return false;
            }
        }
        return true;
    }

    [SupportedOSPlatform("windows")]
    private static bool TryRemove(
        string candidate, bool requireAdminOnly, DateTime utcNow, TimeSpan minimumAge, Action<string, bool> log)
    {
#pragma warning disable CA1031 // Every failure mode here means "leave it alone".
        try
        {
            using (var handle = DirectoryHandle.OpenNoFollow(candidate, InstallSurvivability.UninstallerFileName))
            {
                if (handle is null)
                {
                    return false; // vanished, or not ours to open.
                }
                if (!handle.IsPlainDirectory)
                {
                    Report(log, $"temporary files: '{candidate}' is a reparse point, not a directory; " +
                        "not following it — nothing was deleted", true);
                    return false;
                }
                if (requireAdminOnly && !StateDirectorySecurity.IsAdminOnlyWritableHandle(handle.Handle))
                {
                    return false; // a non-administrator may control it.
                }
                if (utcNow - handle.CreationTimeUtc < minimumAge)
                {
                    return false; // possibly an uninstall staging its copy right now.
                }
            }

            var files = new List<string>();
            foreach (var entry in new DirectoryInfo(candidate).EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                {
                    Report(log, $"temporary files: '{candidate}' holds '{entry.Name}', which is not a plain " +
                        "file; leaving the directory alone", false);
                    return false;
                }
                files.Add(entry.FullName);
            }

            if (!IsWhollyUnused(files))
            {
                Report(log, $"temporary files: '{candidate}' is still in use; leaving it", false);
                return false;
            }

            foreach (var file in files)
            {
                File.Delete(file);
            }
            Directory.Delete(candidate, recursive: false);
            Report(log, $"temporary files: removed '{candidate}', left by an earlier uninstall", false);
            return true;
        }
        catch (Exception ex)
        {
            Report(log, $"temporary files: could not remove '{candidate}' ({ex.Message}); leaving it", false);
            return false;
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// True when every file in <paramref name="files"/> can be opened exclusively at the
    /// same time — none is open elsewhere and none is mapped as a running image.
    /// </summary>
    private static bool IsWhollyUnused(List<string> files)
    {
        var held = new List<FileStream>(files.Count);
        try
        {
            foreach (var file in files)
            {
                held.Add(new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
            }
            return true;
        }
#pragma warning disable CA1031 // Any failure to prove the directory idle answers "in use".
        catch
        {
            return false;
        }
#pragma warning restore CA1031
        finally
        {
            foreach (var stream in held)
            {
                stream.Dispose();
            }
        }
    }

    /// <summary>Write one line to <paramref name="log"/>; a sink that throws loses the line, never the run.</summary>
    private static void Report(Action<string, bool> log, string message, bool isError)
    {
#pragma warning disable CA1031 // A failing log sink must not turn housekeeping into a failed run.
        try
        {
            log(message, isError);
        }
        catch
        {
        }
#pragma warning restore CA1031
    }

    private static string? OwnImageDirectory()
    {
        var image = Environment.ProcessPath;
        return string.IsNullOrEmpty(image) ? null : Path.GetDirectoryName(Path.GetFullPath(image));
    }
}
