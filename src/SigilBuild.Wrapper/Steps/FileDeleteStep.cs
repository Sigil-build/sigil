using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;

namespace SigilBuild.Wrapper.Steps;

/// <summary>
/// Executes an <c>file_delete</c> install step. Before deleting the target,
/// the file's bytes are stashed to a temp location so a rollback can restore
/// the file byte-identical. When the file is already missing the behaviour is
/// controlled by <see cref="InstallStep.FileDelete.IfMissing"/>:
/// <list type="bullet">
///   <item><description><c>skip</c> — succeed silently, record no rollback.</description></item>
///   <item><description><c>fail</c> — return a failure result, record no rollback.</description></item>
/// </list>
/// </summary>
internal sealed class FileDeleteStep : IStep
{
    private readonly InstallStep.FileDelete _spec;

    public FileDeleteStep(InstallStep.FileDelete spec)
    {
        _spec = spec;
    }

    public Task<StepResult> RunAsync(StepContext ctx, RollbackJournal journal, CancellationToken ct)
    {
        var path = ctx.Resolve(_spec.Path);

        if (!File.Exists(path))
        {
            return _spec.IfMissing.Equals("skip", System.StringComparison.OrdinalIgnoreCase)
                ? Task.FromResult(StepResult.Ok())
                : Task.FromResult(StepResult.Failed($"file_delete: target '{path}' does not exist (if_missing=fail)"));
        }

        // Stash the file to a temp location before deletion so rollback can restore it.
        var stash = Path.Combine(Path.GetTempPath(), $"sigil-fd-{System.Guid.NewGuid():N}");
        File.Copy(path, stash, overwrite: false);

        // Register rollback BEFORE the delete so a crash mid-step leaves the journal correct.
        journal.Append(new RollbackRecord.RestoreDeletedFile(path, stash));

        File.Delete(path);

        return Task.FromResult(StepResult.Ok());
    }
}
