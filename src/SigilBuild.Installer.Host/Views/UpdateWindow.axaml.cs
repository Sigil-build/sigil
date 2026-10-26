// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using SigilBuild.Installer.Host.ViewModels;

namespace SigilBuild.Installer.Host.Views;

/// <summary>
/// The headed, non-silent <c>/Update</c> window: a minimal branded
/// progress → up-to-date/done/failed flow, separate from the install
/// <see cref="InstallerWindow"/> and the <see cref="UninstallWindow"/>. State-driven
/// panels are toggled by the view-model's <c>IsProgress</c>/<c>IsUpToDate</c>/
/// <c>IsDone</c>/<c>IsFailed</c> flags. Unlike <see cref="UninstallWindow"/> there is
/// no confirm gesture — the view-model starts checking as soon as it is constructed
/// (see <c>App.axaml.cs</c>), so this code-behind only wires the terminal Close button
/// and the rail drag. It has the same title bar as the other two windows. (R104)
/// </summary>
public partial class UpdateWindow : Window
{
    public UpdateWindow()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, __) =>
            BrandWindowIcon.Apply(this, (DataContext as UpdateViewModel)?.Brand.LogoImage);
    }

    private void OnClose(object? _, RoutedEventArgs __) => Close();

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
