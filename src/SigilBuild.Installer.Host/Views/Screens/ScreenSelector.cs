using Avalonia.Controls;
using Avalonia.Controls.Templates;
using SigilBuild.Installer.Host.ViewModels;

namespace SigilBuild.Installer.Host.Views.Screens;

public sealed class ScreenSelector : IDataTemplate
{
    public bool Match(object? data) => data is InstallerViewModel;

    public Control? Build(object? data)
    {
        return ((InstallerViewModel?)data)?.CurrentStepDef switch
        {
            InstallerStepDef.Welcome => new WelcomeView(),
            InstallerStepDef.License => new LicenseView(),
            InstallerStepDef.InstallDir => new InstallDirView(),
            InstallerStepDef.ParameterGroup => new InstallOptionsView(),
            InstallerStepDef.Installing => new InstallingView(),
            InstallerStepDef.Finish => new FinishView(),
            InstallerStepDef.Custom => new CustomView(),
            _ => new TextBlock { Text = "(no view)" },
        };
    }
}
