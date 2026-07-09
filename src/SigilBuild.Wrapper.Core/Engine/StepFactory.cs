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
        InstallStep.FileCopy fc           => new FileCopyStep(fc),
        InstallStep.DirectoryCreate dc    => new DirectoryCreateStep(dc),
        InstallStep.FileDelete fd         => new FileDeleteStep(fd),
        InstallStep.DirectoryDelete dd    => new DirectoryDeleteStep(dd),
#pragma warning disable CA1416 // Wrapper RID is win-x64; Windows-only steps guard via OperatingSystem.IsWindows().
        InstallStep.RegistryWrite rw        => new RegistryWriteStep(rw),
        InstallStep.RegistryDeleteValue rdv => new RegistryDeleteValueStep(rdv),
        InstallStep.RegistryDeleteKey rdk   => new RegistryDeleteKeyStep(rdk),
        InstallStep.ShortcutCreate sc       => new ShortcutCreateStep(sc),
        InstallStep.EnvSet es               => new EnvSetStep(es),
#pragma warning restore CA1416
        InstallStep.RunProgram rp      => new RunProgramStep(rp),
        _ => throw new System.NotSupportedException(
            $"step type '{spec.GetType().Name}' is not implemented in the current sprint."),
    };
}
