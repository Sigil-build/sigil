using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Steps;

namespace SigilBuild.Wrapper.Engine;

internal static class StepFactory
{
    public static IStep Create(InstallStep spec) => spec switch
    {
        InstallStep.FileCopy fc        => new FileCopyStep(fc),
        InstallStep.DirectoryCreate dc => new DirectoryCreateStep(dc),
        _ => throw new System.NotSupportedException(
            $"step type '{spec.GetType().Name}' is not implemented in Task 11; lands in Task 15-17."),
    };
}
