using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;

namespace SigilBuild.Wrapper.Steps;

/// <summary>
/// Executes a <c>directory_delete</c> install step. Before deleting the
/// subtree, the entire directory is copied to a temp location so rollback can
/// restore all files byte-identical. When the directory is already missing the
/// step succeeds silently without recording a rollback action.
/// <para>
/// When <see cref="InstallStep.DirectoryDelete.Recursive"/> is <c>false</c>
/// the step fails with a descriptive error if the directory is non-empty,
/// matching the semantics of <c>Directory.Delete(path, false)</c> but without
/// leaving partial state.
/// </para>
/// </summary>
internal sealed class DirectoryDeleteStep : IStep
{
    private readonly InstallStep.DirectoryDelete _spec;

    public DirectoryDeleteStep(InstallStep.DirectoryDelete spec)
    {
        _spec = spec;
    }

    public Task<StepResult> RunAsync(StepContext ctx, RollbackJournal journal, CancellationToken ct)
    {
        var path = ctx.Resolve(_spec.Path);

        if (!Directory.Exists(path))
        {
            // Already absent — treat as success, nothing to roll back.
            return Task.FromResult(StepResult.Ok());
        }

        // Non-recursive mode refuses non-empty directories to avoid partial deletes.
        if (!_spec.Recursive && Directory.EnumerateFileSystemEntries(path).Any())
        {
            return Task.FromResult(StepResult.Failed(
                $"directory_delete: '{path}' is not empty and recursive=false"));
        }

        // Stash the entire subtree to a temp location so rollback can restore it.
        var stash = Path.Combine(Path.GetTempPath(), $"sigil-dd-{System.Guid.NewGuid():N}");
        CopyDirectoryRecursive(path, stash);

        // Register rollback BEFORE deletion so a crash mid-step leaves the journal consistent.
        journal.Append(new RollbackRecord.RestoreDeletedDirectory(path, stash));

        Directory.Delete(path, recursive: true);

        return Task.FromResult(StepResult.Ok());
    }

    private static void CopyDirectoryRecursive(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly))
        {
            var rel = Path.GetFileName(file);
            File.Copy(file, Path.Combine(destination, rel), overwrite: false);
        }
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.TopDirectoryOnly))
        {
            var rel = Path.GetFileName(dir);
            CopyDirectoryRecursive(dir, Path.Combine(destination, rel));
        }
    }
}
