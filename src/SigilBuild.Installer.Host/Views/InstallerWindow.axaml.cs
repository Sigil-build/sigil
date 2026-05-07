using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using SigilBuild.Installer.Host.ViewModels;

namespace SigilBuild.Installer.Host.Views;

public partial class InstallerWindow : Window
{
    public InstallerWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnNext(object? _, RoutedEventArgs __) => (DataContext as InstallerViewModel)?.Next();
    private void OnBack(object? _, RoutedEventArgs __) => (DataContext as InstallerViewModel)?.Back();
    private void OnCancel(object? _, RoutedEventArgs __) => Close();
}
