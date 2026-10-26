// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using SigilBuild.Installer.Host.ViewModels;

namespace SigilBuild.Installer.Host.Views;

/// <summary>
/// The interactive uninstall window: a minimal branded
/// <c>confirm → [close apps] → progress → done</c> flow, separate from the install
/// <see cref="InstallerWindow"/>. State-driven panels are toggled by the
/// view-model's <c>IsConfirm</c>/<c>IsCloseApps</c>/<c>IsProgress</c>/<c>IsDone</c>/<c>IsFailed</c>
/// flags, so no per-step template swap is needed. The Close-applications panel is
/// the install wizard's <c>CloseAppsView</c>. (R103)
/// </summary>
/// <remarks>
/// The window has the same title bar as the install wizard, so it can be moved and
/// closed like any other window. (R104)
/// </remarks>
public partial class UninstallWindow : Window
{
    public UninstallWindow()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, __) =>
            BrandWindowIcon.Apply(this, (DataContext as UninstallViewModel)?.Brand.LogoImage);
        Closing += OnClosing;
    }

    private void OnUninstall(object? _, RoutedEventArgs __)
        => (DataContext as UninstallViewModel)?.Confirm();

    private void OnCancel(object? _, RoutedEventArgs __)
    {
        (DataContext as UninstallViewModel)?.Cancel();
        Close();
    }

    private void OnClose(object? _, RoutedEventArgs __) => Close();

    /// <summary>
    /// The OS close gesture — the title bar's X, Alt+F4, the taskbar's Close. Before
    /// the replay starts it means Cancel (exit 2, nothing removed); on Done / Failed it
    /// just closes. While the journal replays it is refused: the replay cannot be
    /// stopped half-way without leaving a partly removed install. (R104)
    /// </summary>
    private void OnClosing(object? _, WindowClosingEventArgs e)
    {
        if (DataContext is not UninstallViewModel vm)
        {
            return;
        }
        if (vm.IsProgress)
        {
            e.Cancel = true;
            return;
        }
        vm.Cancel();
    }

    /// <summary>Drags the window by its rail, as the install wizard does. (R104)</summary>
    private void OnChromePressed(object? _, PointerPressedEventArgs e)
    {
        // Left button only: a right-press here is not a drag, and beginning one
        // would swallow the gesture.
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }
}
