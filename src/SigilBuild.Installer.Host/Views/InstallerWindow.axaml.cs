using System.Threading.Tasks;
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

    private async void OnCancel(object? _, RoutedEventArgs __) => await TryCloseWithCancelAsync();

    /// <summary>Called by the Cancel button and by the window-close gesture.</summary>
    private async Task TryCloseWithCancelAsync()
    {
        if (DataContext is not InstallerViewModel vm)
        {
            Close();
            return;
        }

        var confirmed = await vm.CancelAsync(() => ShowConfirmDialogAsync());
        if (confirmed)
            Close();
    }

    private async Task<bool> ShowConfirmDialogAsync()
    {
        var dialog = new CancelConfirmDialog();
        var result = await dialog.ShowDialog<bool?>(this);
        return result is true;
    }
}
