// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using SigilBuild.Installer.Host.ViewModels;

namespace SigilBuild.Installer.Host.Views.Screens;

/// <summary>
/// The "Close applications" gate. Lists what is holding the install
/// directory and offers Retry (the user closed them) or Close-for-me (Restart
/// Manager graceful shutdown). Cancel is the shared footer button.
/// </summary>
public partial class CloseAppsView : UserControl
{
    public CloseAppsView() { AvaloniaXamlLoader.Load(this); }

    private void OnRetry(object? _, RoutedEventArgs __)
        => (DataContext as InstallerViewModel)?.RetryBlockers();

    // Not awaited: the view-model runs the close off the UI thread, owns the busy
    // state and handles its own failures, so the click returns at once. (R94)
    private void OnCloseForMe(object? _, RoutedEventArgs __)
        => _ = (DataContext as InstallerViewModel)?.CloseBlockingAppsAsync();
}
