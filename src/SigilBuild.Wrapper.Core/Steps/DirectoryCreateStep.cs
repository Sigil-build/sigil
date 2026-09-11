using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;

namespace SigilBuild.Wrapper.Steps;

internal sealed class DirectoryCreateStep : IStep
{
    private readonly InstallStep.DirectoryCreate _spec;

    public DirectoryCreateStep(InstallStep.DirectoryCreate spec)
    {
        _spec = spec;
    }

    public Task<StepResult> RunAsync(StepContext ctx, RollbackJournal journal, CancellationToken ct)
    {
        // ResolvePath refuses a path that still carries an unresolved {token}, so
        // this step cannot materialize a directory literally named "{var.typo}" (R16).
        var path = ctx.ResolvePath(_spec.Path);

        // Directory.CreateDirectory builds the whole tree, so an unanchored path
        // lets a manifest create directories anywhere the elevated installer can
        // reach. Refused before the journal entry: nothing was created, so there is
        // nothing to remove on rollback (R16).
        var refusal = StepDestinationGuard.Check(
            ctx.InstallDir, "directory_create", "path", path, _spec.AllowOutsideInstallDir);
        if (refusal is not null)
        {
            return Task.FromResult(StepResult.Failed(refusal));
        }

        if (!Directory.Exists(path))
        {
            // Record rollback for this newly-created directory only.
            journal.Append(new RollbackRecord.RemoveDirectory(path));
            Directory.CreateDirectory(path);
        }
        return Task.FromResult(StepResult.Ok());
    }
}
