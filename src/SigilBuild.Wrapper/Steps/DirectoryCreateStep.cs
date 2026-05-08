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
        var path = ctx.Resolve(_spec.Path);
        if (!Directory.Exists(path))
        {
            // Record rollback for this newly-created directory only.
            journal.Append(new RollbackRecord.RemoveDirectory(path));
            Directory.CreateDirectory(path);
        }
        return Task.FromResult(StepResult.Ok());
    }
}
