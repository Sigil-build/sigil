using Avalonia.Controls;
using Avalonia.Controls.Templates;
using SigilBuild.Installer.Host.ViewModels;

namespace SigilBuild.Installer.Host.Views.Screens;

public sealed class ScreenSelector : IDataTemplate
{
    public bool Match(object? data) => data is InstallerViewModel;

    public Control? Build(object? data)
    {
        return ((InstallerViewModel?)data)?.CurrentStep switch
        {
            InstallerStep.Welcome => new WelcomeView(),
            InstallerStep.License => new LicenseView(),
            InstallerStep.InstallOptions => new InstallOptionsView(),
            InstallerStep.Options => new OptionsView(),
            InstallerStep.Installing => new InstallingView(),
            InstallerStep.Failed => new FailedView(),
            InstallerStep.Finish => new FinishView(),
            InstallerStep.Custom => new CustomView(),
            _ => new TextBlock { Text = "(no view)" },
        };
    }
}
