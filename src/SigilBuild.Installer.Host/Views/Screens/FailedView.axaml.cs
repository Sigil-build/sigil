using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using SigilBuild.Installer.Host.ViewModels;

namespace SigilBuild.Installer.Host.Views.Screens;

public partial class FailedView : UserControl
{
    public FailedView() { AvaloniaXamlLoader.Load(this); }

    // P7: open the /LOG install log in the OS default handler.
    private void OnOpenLogClick(object? sender, RoutedEventArgs e)
        => (DataContext as InstallerViewModel)?.OpenLog();
}
