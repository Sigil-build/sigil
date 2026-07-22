using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using SigilBuild.Installer.Host.ViewModels;

namespace SigilBuild.Installer.Host.Views;

public partial class InstallerWindow : Window
{
    private InstallerViewModel? _observed;

    public InstallerWindow()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += OnDataContextChanged;
        // P2 (gap G4): when the wizard closes on the Done screen, launch the app if
        // the checked-by-default "Launch <App>" box is ticked. Fires on any close
        // (Finish, X gesture) — the VM gates on OutcomeCode==Completed so a
        // cancelled / failed run never launches.
        Closed += (_, __) => (DataContext as InstallerViewModel)?.LaunchIfRequested();
    }

    // The ContentControl binds its Content to the (single, unchanging) view-model,
    // so a CurrentStep change alone won't re-run the ScreenSelector template.
    // Observe the VM and force the content host to rebuild on each step change.
    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_observed is not null)
        {
            _observed.PropertyChanged -= OnVmPropertyChanged;
        }
        _observed = DataContext as InstallerViewModel;
        if (_observed is not null)
        {
            _observed.PropertyChanged += OnVmPropertyChanged;
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(InstallerViewModel.CurrentStep))
        {
            return;
        }
        var host = this.FindControl<ContentControl>("ScreenHost");
        if (host is not null)
        {
            // Toggle Content so the ContentPresenter re-applies the ScreenSelector
            // template for the new CurrentStep.
            host.Content = null;
            host.Content = DataContext;
        }
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
