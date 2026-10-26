// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using Avalonia.Controls;
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
public partial class UninstallWindow : Window
{
    public UninstallWindow()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, __) =>
            BrandWindowIcon.Apply(this, (DataContext as UninstallViewModel)?.Brand.LogoImage);
    }

    private void OnUninstall(object? _, RoutedEventArgs __)
        => (DataContext as UninstallViewModel)?.Confirm();

    private void OnCancel(object? _, RoutedEventArgs __)
    {
        (DataContext as UninstallViewModel)?.Cancel();
        Close();
    }

    private void OnClose(object? _, RoutedEventArgs __) => Close();
}
