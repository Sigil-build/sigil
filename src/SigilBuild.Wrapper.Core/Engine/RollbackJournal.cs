using System.Linq;
using System.Runtime.Versioning;
using Microsoft.Win32;
using SigilBuild.Wrapper.Steps;

namespace SigilBuild.Wrapper.Engine;

/// <summary>
/// Append-only log of rollback actions recorded by individual steps as they
/// mutate the system. <see cref="UndoAsync"/> walks the records in reverse
/// (LIFO) on a failed install. Future tasks (19) serialize the journal as
/// <c>uninstall.json</c> for post-install removal.
/// </summary>
public sealed class RollbackJournal
{
    private readonly System.Collections.Generic.List<RollbackRecord> _records = new();

    public System.Collections.Generic.IReadOnlyList<RollbackRecord> Records => _records;

    public void Append(RollbackRecord record)
    {
        System.ArgumentNullException.ThrowIfNull(record);
        _records.Add(record);
    }

    /// <summary>
    /// Delete the transient install-time <em>stash</em> artefacts once the install
    /// has COMMITTED successfully. A <c>file_delete</c> / <c>directory_delete</c>
    /// step copies its target to a <c>%TEMP%</c> stash (<c>sigil-fd-*</c> /
    /// <c>sigil-dd-*</c>) so a mid-install rollback can restore the original; the
    /// stash is normally reclaimed by <see cref="UndoAsync"/> on rollback. On the
    /// SUCCESS path no rollback runs, so without this call the stash leaks into
    /// <c>%TEMP%</c> forever (an empty <c>sigil-dd-*</c> directory for an
    /// empty-directory delete, a stray <c>sigil-fd-*</c> file otherwise).
    /// <para>
    /// Discarding is safe: the two stash-bearing records
    /// (<see cref="RollbackRecord.RestoreDeletedFile"/> /
    /// <see cref="RollbackRecord.RestoreDeletedDirectory"/>) are NOT part of the
    /// persisted <c>uninstall.json</c> schema (they have no
    /// <c>SerializableRollbackRecord</c> mapping), so their <c>%TEMP%</c> stash was
    /// never meant to outlive the install run. Best-effort and idempotent.
    /// </para>
    /// </summary>
    public void DiscardTransientStashes()
    {
        foreach (var record in _records)
        {
            switch (record)
            {
                case RollbackRecord.RestoreDeletedFile f:
                    TryDeleteFile(f.StashPath);
                    break;
                case RollbackRecord.RestoreDeletedDirectory d:
                    TryDeleteDirectory(d.StashPath);
                    break;
                case RollbackRecord.RestoreConfigFile { StashPath: { } cfgStash }:
                    // P8: the prior-content stash of an ini/json/xml edit — reclaim
                    // it on the success path (a config edit isn't reversed on uninstall).
                    TryDeleteFile(cfgStash);
                    break;
                default:
                    break;
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
#pragma warning disable CA1031 // Best-effort temp cleanup; a leftover stash is harmless.
        try
        {
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
        catch { /* best-effort */ }
#pragma warning restore CA1031
    }

    private static void TryDeleteDirectory(string path)
    {
#pragma warning disable CA1031 // Best-effort temp cleanup; a leftover stash is harmless.
        try
        {
            if (System.IO.Directory.Exists(path))
            {
                System.IO.Directory.Delete(path, recursive: true);
            }
        }
        catch { /* best-effort */ }
#pragma warning restore CA1031
    }

    public async System.Threading.Tasks.Task UndoAsync(
        System.Threading.CancellationToken ct,
        System.IProgress<StepProgress>? progress = null)
    {
        // Walk in reverse. Undo failures should not cascade — log and continue.
        var total = _records.Count;
        var completed = 0;
        for (var i = _records.Count - 1; i >= 0; i--)
        {
            ct.ThrowIfCancellationRequested();
            var record = _records[i];
            try
            {
                await record.UndoAsync(ct).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Best-effort undo: individual failures must not cascade.
            catch
            {
                // Best-effort; swallow individual undo failures.
            }
#pragma warning restore CA1031
            completed++;
            progress?.Report(new StepProgress(completed, total, DescribeUndo(record), IsError: false));
        }
    }

    /// <summary>
    /// A short, prototype-style reversal line for the interactive uninstall log
    /// (spec T15 / design brief: <c>unlink</c>, <c>path -</c>, <c>reg -</c>,
    /// <c>delete</c>). Derived from the record's declared fields only — no resolved
    /// parameter values, so a secret can never leak into the uninstall log.
    /// </summary>
    private static string DescribeUndo(RollbackRecord record) => record switch
    {
        RollbackRecord.RestoreFile r => $"delete {r.Path}",
        RollbackRecord.RemoveDirectory r => $"rmdir {r.Path}",
        RollbackRecord.DeleteShortcut r => $"unlink {r.Path}",
        RollbackRecord.RestoreRegistryValue r => $"reg - {r.Key}\\{r.Name}",
        RollbackRecord.RestoreRegistryKey r => $"reg - {r.Key}",
        RollbackRecord.RestoreEnv r => r.Name.Equals("PATH", System.StringComparison.OrdinalIgnoreCase)
            ? "path -"
            : $"env - {r.Name}",
        RollbackRecord.RestoreDeletedFile r => $"restore {r.OriginalPath}",
        RollbackRecord.RestoreDeletedDirectory r => $"restore {r.OriginalPath}",
        RollbackRecord.RestoreConfigFile r => $"restore {r.OriginalPath}",
        RollbackRecord.RemoveUninstaller r => $"delete {r.Path}",
        RollbackRecord.DeleteScheduledTask r => $"deltask {r.TaskName}",
        _ => "revert",
    };
}

public abstract record RollbackRecord
{
    public abstract System.Threading.Tasks.Task UndoAsync(System.Threading.CancellationToken ct);

    public sealed record RestoreFile(string Path, bool ExistedBefore, string? BackupPath) : RollbackRecord
    {
        public override System.Threading.Tasks.Task UndoAsync(System.Threading.CancellationToken ct)
        {
            if (ExistedBefore && BackupPath is not null && System.IO.File.Exists(BackupPath))
            {
                System.IO.File.Copy(BackupPath, Path, overwrite: true);
                System.IO.File.Delete(BackupPath);
            }
            else if (System.IO.File.Exists(Path))
            {
                System.IO.File.Delete(Path);
            }
            return System.Threading.Tasks.Task.CompletedTask;
        }
    }

    public sealed record RemoveDirectory(string Path) : RollbackRecord
    {
        public override System.Threading.Tasks.Task UndoAsync(System.Threading.CancellationToken ct)
        {
            // Only remove if empty — matches the "create only if previously absent" semantics.
            if (System.IO.Directory.Exists(Path) &&
                !System.IO.Directory.EnumerateFileSystemEntries(Path).Any())
            {
                System.IO.Directory.Delete(Path);
            }
            return System.Threading.Tasks.Task.CompletedTask;
        }
    }

    /// <summary>
    /// Delete a shortcut <c>.lnk</c> that the <c>shortcut_create</c> step
    /// materialised. Best-effort — a missing file (already cleaned up by an
    /// earlier failed save) is treated as success.
    /// </summary>
    public sealed record DeleteShortcut(string Path) : RollbackRecord
    {
        public override System.Threading.Tasks.Task UndoAsync(System.Threading.CancellationToken ct)
        {
            if (System.IO.File.Exists(Path))
            {
#pragma warning disable CA1031 // Best-effort undo — failure to delete a stray .lnk should not cascade.
                try
                {
                    System.IO.File.Delete(Path);
                }
                catch
                {
                    // Best-effort; swallow.
                }
#pragma warning restore CA1031
            }
            return System.Threading.Tasks.Task.CompletedTask;
        }
    }

    /// <summary>
    /// Restore a single registry value to its prior state. If the value was
    /// previously absent the rollback deletes whatever the step wrote;
    /// otherwise it re-writes the captured value with its captured kind.
    /// No-op on non-Windows hosts so the type can travel through the
    /// platform-neutral journal API.
    /// </summary>
    public sealed record RestoreRegistryValue(
        string Hive,
        string Key,
        string Name,
        string View,
        string? PriorTypeStr,
        object? PriorValue,
        bool PreviouslyAbsent) : RollbackRecord
    {
        public override System.Threading.Tasks.Task UndoAsync(System.Threading.CancellationToken ct)
        {
            if (!System.OperatingSystem.IsWindows())
            {
                return System.Threading.Tasks.Task.CompletedTask;
            }

            return UndoOnWindows();
        }

        [SupportedOSPlatform("windows")]
        private System.Threading.Tasks.Task UndoOnWindows()
        {
            var hive = RegistryHelper.ParseHive(Hive);
            var view = RegistryHelper.ParseView(View);

            using var baseKey = RegistryKey.OpenBaseKey(hive, view);

            if (PreviouslyAbsent)
            {
                // The value didn't exist before; if the step created it, scrub it.
                using var sub = baseKey.OpenSubKey(Key, writable: true);
                if (sub is null)
                {
                    return System.Threading.Tasks.Task.CompletedTask;
                }
#pragma warning disable CA1031 // Best-effort undo; missing values are fine.
                try { sub.DeleteValue(Name, throwOnMissingValue: false); }
                catch { /* best-effort */ }
#pragma warning restore CA1031
            }
            else if (PriorValue is not null && PriorTypeStr is not null)
            {
                // Re-create the parent key if the step deleted it (delete_value
                // never deletes the key, but delete_key on a parent could).
                using var sub = baseKey.CreateSubKey(Key, writable: true);
                if (sub is null)
                {
                    return System.Threading.Tasks.Task.CompletedTask;
                }
                sub.SetValue(Name, PriorValue, RegistryHelper.ParseValueKind(PriorTypeStr));
            }
            return System.Threading.Tasks.Task.CompletedTask;
        }
    }

    /// <summary>
    /// Restore a Windows environment variable to its prior state. If
    /// <paramref name="PreviouslyAbsent"/> is true the rollback deletes the
    /// value the step wrote; otherwise it re-writes <paramref name="PriorValue"/>
    /// as <c>REG_SZ</c>. After restoration a best-effort
    /// <c>WM_SETTINGCHANGE</c> broadcast notifies running shells of the
    /// reverted state. No-op on non-Windows hosts so the type can travel
    /// through the platform-neutral journal API.
    /// </summary>
    public sealed record RestoreEnv(
        string Scope,
        string Name,
        string? PriorValue,
        bool PreviouslyAbsent) : RollbackRecord
    {
        public override System.Threading.Tasks.Task UndoAsync(System.Threading.CancellationToken ct)
        {
            if (!System.OperatingSystem.IsWindows())
            {
                return System.Threading.Tasks.Task.CompletedTask;
            }
            UndoOnWindows();
            return System.Threading.Tasks.Task.CompletedTask;
        }

        [SupportedOSPlatform("windows")]
        private void UndoOnWindows()
        {
            using var key = OpenEnvKey(Scope, writable: true);
            if (key is null)
            {
                return;
            }

            if (PreviouslyAbsent)
            {
#pragma warning disable CA1031 // Best-effort undo: a missing value is fine.
                try { key.DeleteValue(Name, throwOnMissingValue: false); }
                catch { /* best-effort */ }
#pragma warning restore CA1031
            }
            else if (PriorValue is not null)
            {
                key.SetValue(Name, PriorValue, RegistryValueKind.String);
            }

            EnvBroadcast.NotifySettingChange();
        }

        [SupportedOSPlatform("windows")]
        private static RegistryKey? OpenEnvKey(string scope, bool writable) => scope switch
        {
            "user"    => Registry.CurrentUser.OpenSubKey("Environment", writable),
            "machine" => Registry.LocalMachine.OpenSubKey(
                @"System\CurrentControlSet\Control\Session Manager\Environment", writable),
            _ => throw new System.ArgumentException($"unknown env scope '{scope}'"),
        };
    }

    /// <summary>
    /// Restore a registry key whose immediate values were captured before
    /// deletion. Recursive subtree restore is an acknowledged gap — only
    /// the values directly under the key are re-created. If the key was
    /// previously absent the rollback is a no-op.
    /// </summary>
    public sealed record RestoreRegistryKey(
        string Hive,
        string Key,
        string View,
        System.Collections.Generic.IReadOnlyList<RegistryValueSnapshot> ValuesAtKeyLevel,
        bool PreviouslyAbsent) : RollbackRecord
    {
        public override System.Threading.Tasks.Task UndoAsync(System.Threading.CancellationToken ct)
        {
            if (!System.OperatingSystem.IsWindows())
            {
                return System.Threading.Tasks.Task.CompletedTask;
            }
            if (PreviouslyAbsent)
            {
                return System.Threading.Tasks.Task.CompletedTask;
            }
            return UndoOnWindows();
        }

        [SupportedOSPlatform("windows")]
        private System.Threading.Tasks.Task UndoOnWindows()
        {
            var hive = RegistryHelper.ParseHive(Hive);
            var view = RegistryHelper.ParseView(View);
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var sub = baseKey.CreateSubKey(Key, writable: true);
            if (sub is null)
            {
                return System.Threading.Tasks.Task.CompletedTask;
            }
            foreach (var snap in ValuesAtKeyLevel)
            {
                if (snap.Value is null)
                {
                    continue;
                }
                sub.SetValue(snap.Name, snap.Value, RegistryHelper.ParseValueKind(snap.TypeStr));
            }
            return System.Threading.Tasks.Task.CompletedTask;
        }
    }

    /// <summary>
    /// Restores a file that was deleted by <c>file_delete</c>. The bytes were
    /// stashed to a temp path before deletion; rollback copies them back.
    /// If the stash is gone (already cleaned up) the record is a no-op.
    /// </summary>
    public sealed record RestoreDeletedFile(string OriginalPath, string StashPath) : RollbackRecord
    {
        public override System.Threading.Tasks.Task UndoAsync(System.Threading.CancellationToken ct)
        {
            if (System.IO.File.Exists(StashPath))
            {
                System.IO.Directory.CreateDirectory(
                    System.IO.Path.GetDirectoryName(OriginalPath)!);
                System.IO.File.Copy(StashPath, OriginalPath, overwrite: true);
#pragma warning disable CA1031 // Best-effort stash cleanup; a leftover temp file is harmless.
                try { System.IO.File.Delete(StashPath); }
                catch { /* best-effort */ }
#pragma warning restore CA1031
            }
            return System.Threading.Tasks.Task.CompletedTask;
        }
    }

    /// <summary>
    /// Restores a directory subtree that was deleted by <c>directory_delete</c>.
    /// The subtree was copied to <paramref name="StashPath"/> before deletion;
    /// rollback moves it back recursively. If the stash is gone the record is
    /// a no-op.
    /// </summary>
    public sealed record RestoreDeletedDirectory(string OriginalPath, string StashPath) : RollbackRecord
    {
        public override System.Threading.Tasks.Task UndoAsync(System.Threading.CancellationToken ct)
        {
            if (!System.IO.Directory.Exists(StashPath))
            {
                return System.Threading.Tasks.Task.CompletedTask;
            }

            System.IO.Directory.CreateDirectory(OriginalPath);
            CopyDirectoryRecursive(StashPath, OriginalPath);
#pragma warning disable CA1031 // Best-effort stash cleanup.
            try { System.IO.Directory.Delete(StashPath, recursive: true); }
            catch { /* best-effort */ }
#pragma warning restore CA1031
            return System.Threading.Tasks.Task.CompletedTask;
        }

        private static void CopyDirectoryRecursive(string source, string destination)
        {
            foreach (var file in System.IO.Directory.EnumerateFiles(source, "*", System.IO.SearchOption.TopDirectoryOnly))
            {
                var rel = System.IO.Path.GetFileName(file);
                System.IO.File.Copy(file, System.IO.Path.Combine(destination, rel), overwrite: true);
            }
            foreach (var dir in System.IO.Directory.EnumerateDirectories(source, "*", System.IO.SearchOption.TopDirectoryOnly))
            {
                var rel = System.IO.Path.GetFileName(dir);
                var destSub = System.IO.Path.Combine(destination, rel);
                System.IO.Directory.CreateDirectory(destSub);
                CopyDirectoryRecursive(dir, destSub);
            }
        }
    }

    /// <summary>
    /// Restores a config file (P8 <c>ini_write</c> / <c>json_edit</c> /
    /// <c>xml_edit</c>) to its exact pre-edit state on a mid-install rollback. When
    /// the file existed before the edit, its whole content was stashed to
    /// <paramref name="StashPath"/> and rollback copies it back byte-for-byte; when
    /// the file did NOT exist (a <c>create_if_missing</c> edit), <paramref name="StashPath"/>
    /// is <c>null</c> and rollback deletes the file the edit created. The stash is
    /// reclaimed by <see cref="DiscardTransientStashes"/> on a successful install,
    /// so a committed config edit is not reverted at uninstall time.
    /// </summary>
    public sealed record RestoreConfigFile(string OriginalPath, string? StashPath) : RollbackRecord
    {
        public override System.Threading.Tasks.Task UndoAsync(System.Threading.CancellationToken ct)
        {
            if (StashPath is not null && System.IO.File.Exists(StashPath))
            {
                var dir = System.IO.Path.GetDirectoryName(OriginalPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    System.IO.Directory.CreateDirectory(dir);
                }
                System.IO.File.Copy(StashPath, OriginalPath, overwrite: true);
#pragma warning disable CA1031 // Best-effort stash cleanup; a leftover temp file is harmless.
                try { System.IO.File.Delete(StashPath); }
                catch { /* best-effort */ }
#pragma warning restore CA1031
            }
            else if (StashPath is null && System.IO.File.Exists(OriginalPath))
            {
                // The edit created this file; undo removes it.
#pragma warning disable CA1031 // Best-effort undo; a leftover created file is preferable to a crash.
                try { System.IO.File.Delete(OriginalPath); }
                catch { /* best-effort */ }
#pragma warning restore CA1031
            }
            return System.Threading.Tasks.Task.CompletedTask;
        }
    }

    /// <summary>
    /// Removes the survivable <c>uninstall.exe</c> copied into the install dir as
    /// the final install step (spec T15). Undo is delegated to
    /// <see cref="SelfDelete"/>, which tolerates the case where <see cref="Path"/>
    /// is the <em>running</em> uninstaller image: it cannot delete its own live
    /// image, so it schedules reboot-time deletion instead. Journal replay never
    /// aborts on this entry — the delete is best-effort and never throws.
    /// </summary>
    public sealed record RemoveUninstaller(string Path) : RollbackRecord
    {
        public override System.Threading.Tasks.Task UndoAsync(System.Threading.CancellationToken ct)
        {
            SelfDelete.Remove(Path);
            return System.Threading.Tasks.Task.CompletedTask;
        }
    }

    /// <summary>
    /// Stops + deletes a Windows service created by <c>service_install</c>.
    /// Recorded BEFORE the create so an interrupted install can still unwind.
    /// On uninstall this runs as part of the journal replay. sc.exe absence /
    /// service-not-found is silently tolerated — the goal is "no service after
    /// rollback," not "exact symmetric command execution."
    /// </summary>
    public sealed record RemoveService(string ServiceName) : RollbackRecord
    {
        public override async System.Threading.Tasks.Task UndoAsync(System.Threading.CancellationToken ct)
        {
            await RunScAsync(new[] { "stop", ServiceName }, ct).ConfigureAwait(false);
            await RunScAsync(new[] { "delete", ServiceName }, ct).ConfigureAwait(false);
        }

        private static async System.Threading.Tasks.Task RunScAsync(string[] args, System.Threading.CancellationToken ct)
        {
            var psi = new System.Diagnostics.ProcessStartInfo("sc.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            try
            {
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc is null) return;
                await proc.WaitForExitAsync(ct).ConfigureAwait(false);
                // sc.exe exit 1060 = service does not exist; benign on rollback.
            }
#pragma warning disable CA1031 // Best-effort uninstall — sc.exe missing or admin denied is acceptable.
            catch
            {
            }
#pragma warning restore CA1031
        }
    }

    /// <summary>
    /// Deletes a Windows Scheduled Task created by <c>scheduled_task_create</c>
    /// (P11, T11.1). Recorded BEFORE the create so an interrupted install can
    /// still unwind. Mirrors <see cref="RemoveService"/>: schtasks.exe absence /
    /// "task not found" is silently tolerated — the goal is "no task after
    /// rollback," not "exact symmetric command execution." Only the task NAME
    /// is carried — no secrets, no resolved program path.
    /// </summary>
    public sealed record DeleteScheduledTask(string TaskName) : RollbackRecord
    {
        public override async System.Threading.Tasks.Task UndoAsync(System.Threading.CancellationToken ct)
        {
            var psi = new System.Diagnostics.ProcessStartInfo("schtasks.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("/Delete");
            psi.ArgumentList.Add("/TN");
            psi.ArgumentList.Add(TaskName);
            psi.ArgumentList.Add("/F");
            try
            {
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc is null) return;
                await proc.WaitForExitAsync(ct).ConfigureAwait(false);
                // schtasks /Delete on a missing task exits non-zero ("The
                // system cannot find the file specified" / ERROR_FILE_NOT_FOUND);
                // benign on rollback, same tolerance as RemoveService above.
            }
#pragma warning disable CA1031 // Best-effort uninstall — schtasks.exe missing or admin denied is acceptable.
            catch
            {
            }
#pragma warning restore CA1031
        }
    }
}

/// <summary>
/// Captured state of a single registry value at the moment it was
/// snapshotted by <see cref="RollbackRecord.RestoreRegistryKey"/>. Held
/// outside the record's parameter list to keep the public API readable.
/// </summary>
public readonly record struct RegistryValueSnapshot(string Name, string TypeStr, object? Value);
