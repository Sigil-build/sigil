// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

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
        // The OS close gesture — the title bar's X, Alt+F4, the taskbar's Close —
        // must mean what Cancel means, or it is a way to abandon a running install
        // with no confirmation and no rollback. Nothing listened for it before, and
        // Alt+F4 already reached a window that had no X to click. (Wizard walkthrough)
        Closing += OnClosing;
        // When the wizard closes on the Done screen, launch the app if
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
            BrandWindowIcon.Apply(this, _observed.Brand.LogoImage);
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

    /// <summary>
    /// The terminal footer's Close button (Finish / Failed / DowngradeBlocked).
    /// Closes directly rather than through <see cref="InstallerViewModel.CancelAsync"/>,
    /// which returns <c>false</c> on Finish by design. The process exit code is
    /// already decided by then — <c>App.OutcomeExitCode</c> reads the view-model's
    /// <c>OutcomeCode</c>, set when the engine finished — so closing here cannot
    /// downgrade a failure to a success.
    /// </summary>
    private void OnClose(object? _, RoutedEventArgs __)
    {
        _closeApproved = true;
        Close();
    }

    /// <summary>
    /// Set by the paths that have already decided the window may close, so
    /// <see cref="OnClosing"/> lets their <c>Close()</c> through instead of asking
    /// the same question a second time.
    /// </summary>
    private bool _closeApproved;

    /// <summary>
    /// The OS close gesture. Routed through the same decision as the Cancel button:
    /// on a terminal screen it just closes, during an install it must confirm, and a
    /// declined confirmation leaves the window open and the install running.
    /// </summary>
    private async void OnClosing(object? _, WindowClosingEventArgs e)
    {
        if (_closeApproved || DataContext is not InstallerViewModel vm)
        {
            return;
        }

        // Finish / Failed / DowngradeBlocked: nothing to cancel, and CancelAsync
        // returns false on Finish by design — routing there would make the X refuse
        // to close a finished installer, which is the trap this whole change is
        // about.
        if (vm.IsTerminal)
        {
            return;
        }

        // The decision is asynchronous (it may show a modal), and Closing cannot
        // await. Stop this close, then re-issue it once the answer is in.
        e.Cancel = true;
        await TryCloseWithCancelAsync();
    }

    /// <summary>
    /// Drags the window by its rail. <c>WindowDecorations="BorderOnly"</c> leaves
    /// no title bar, so without this the window cannot be moved at all — it opens
    /// centred and stays there, on top of whatever the user was reading.
    /// </summary>
    private void OnChromePressed(object? _, Avalonia.Input.PointerPressedEventArgs e)
    {
        // Left button only: a right-press here is not a drag, and beginning one
        // would swallow the gesture.
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    /// <summary>Called by the Cancel button and by the window-close gesture.</summary>
    private async Task TryCloseWithCancelAsync()
    {
        if (DataContext is not InstallerViewModel vm)
        {
            _closeApproved = true;
            Close();
            return;
        }

        var confirmed = await vm.CancelAsync(() => ShowConfirmDialogAsync());
        if (confirmed)
        {
            _closeApproved = true;
            Close();
        }
    }

    private async Task<bool> ShowConfirmDialogAsync()
    {
        // Inherit the owner's icon rather than resolving the brand a second time:
        // the dialog has no view model, and a modal that shows the toolkit's
        // default mark next to a window showing the product's reads as something
        // that came from somewhere else — the opposite of what a confirmation
        // prompt needs to convey.
        var dialog = new CancelConfirmDialog { Icon = Icon };
        var result = await dialog.ShowDialog<bool?>(this);
        return result is true;
    }
}
