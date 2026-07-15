using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using SigilBuild.Installer.Host.ViewModels;

namespace SigilBuild.Installer.Host.Views.Screens;

/// <summary>
/// P6 (gap G7): the "Close applications" gate. Lists what is holding the install
/// directory and offers Retry (the user closed them) or Close-for-me (Restart
/// Manager graceful shutdown). Cancel is the shared footer button.
/// </summary>
public partial class CloseAppsView : UserControl
{
    public CloseAppsView() { AvaloniaXamlLoader.Load(this); }

    private void OnRetry(object? _, RoutedEventArgs __)
        => (DataContext as InstallerViewModel)?.RetryBlockers();

    private void OnCloseForMe(object? _, RoutedEventArgs __)
        => (DataContext as InstallerViewModel)?.CloseBlockingApps();
}
