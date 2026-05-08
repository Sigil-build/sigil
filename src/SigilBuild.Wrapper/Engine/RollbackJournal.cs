using System.Linq;

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
}
