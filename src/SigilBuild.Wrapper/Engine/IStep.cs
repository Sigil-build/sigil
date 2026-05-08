namespace SigilBuild.Wrapper.Engine;

internal interface IStep
{
    System.Threading.Tasks.Task<StepResult> RunAsync(
        StepContext ctx,
        RollbackJournal journal,
        System.Threading.CancellationToken ct);
}
