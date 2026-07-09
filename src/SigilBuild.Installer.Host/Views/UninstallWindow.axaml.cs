using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using SigilBuild.Installer.Host.ViewModels;

namespace SigilBuild.Installer.Host.Views;

/// <summary>
/// The interactive uninstall window (spec T15): a minimal branded
/// <c>confirm → progress → done</c> flow, separate from the install
/// <see cref="InstallerWindow"/>. State-driven panels are toggled by the
/// view-model's <c>IsConfirm</c>/<c>IsProgress</c>/<c>IsDone</c>/<c>IsFailed</c>
/// flags, so no per-step template swap is needed.
/// </summary>
public partial class UninstallWindow : Window
{
    public UninstallWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnUninstall(object? _, RoutedEventArgs __)
        => (DataContext as UninstallViewModel)?.Confirm();

    private void OnCancelConfirm(object? _, RoutedEventArgs __)
    {
        (DataContext as UninstallViewModel)?.CancelConfirm();
        Close();
    }

    private void OnClose(object? _, RoutedEventArgs __) => Close();
}
