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

    public async System.Threading.Tasks.Task UndoAsync(System.Threading.CancellationToken ct)
    {
        // Walk in reverse. Undo failures should not cascade — log and continue.
        for (var i = _records.Count - 1; i >= 0; i--)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await _records[i].UndoAsync(ct).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Best-effort undo: individual failures must not cascade.
            catch
            {
                // Best-effort; swallow individual undo failures.
            }
#pragma warning restore CA1031
        }
    }
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
}

/// <summary>
/// Captured state of a single registry value at the moment it was
/// snapshotted by <see cref="RollbackRecord.RestoreRegistryKey"/>. Held
/// outside the record's parameter list to keep the public API readable.
/// </summary>
public readonly record struct RegistryValueSnapshot(string Name, string TypeStr, object? Value);
