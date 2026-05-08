using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Steps;

namespace SigilBuild.Wrapper.Engine;

internal static class StepFactory
{
    // Note: registry steps are [SupportedOSPlatform("windows")]. The wrapper
    // ships only with RID=win-x64, so the call sites here are de-facto
    // Windows. The CA1416 suppressions document that contract; each step's
    // own RunAsync still re-checks via OperatingSystem.IsWindows().
    public static IStep Create(InstallStep spec) => spec switch
    {
        InstallStep.FileCopy fc        => new FileCopyStep(fc),
        InstallStep.DirectoryCreate dc => new DirectoryCreateStep(dc),
#pragma warning disable CA1416 // Wrapper RID is win-x64; registry steps guard via OperatingSystem.IsWindows().
        InstallStep.RegistryWrite rw        => new RegistryWriteStep(rw),
        InstallStep.RegistryDeleteValue rdv => new RegistryDeleteValueStep(rdv),
        InstallStep.RegistryDeleteKey rdk   => new RegistryDeleteKeyStep(rdk),
#pragma warning restore CA1416
        _ => throw new System.NotSupportedException(
            $"step type '{spec.GetType().Name}' is not implemented in Task 11/15; lands in Task 16-17."),
    };
}
