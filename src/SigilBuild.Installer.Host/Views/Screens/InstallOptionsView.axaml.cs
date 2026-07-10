using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using SigilBuild.Installer.Host.ViewModels;

namespace SigilBuild.Installer.Host.Views.Screens;

/// <summary>
/// The Destination screen (T13): install-location input + Browse folder picker and
/// the T12 user/machine scope radios (when the manifest scope is <c>auto</c>). The
/// collected path becomes <c>{install_dir}</c>; validation is driven by the
/// view-model (<see cref="InstallerViewModel.ValidateDestination"/>).
/// </summary>
public partial class InstallOptionsView : UserControl
{
    public InstallOptionsView() { AvaloniaXamlLoader.Load(this); }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not InstallerViewModel vm)
        {
            return;
        }

        var top = TopLevel.GetTopLevel(this);
        if (top is null)
        {
            return;
        }

#pragma warning disable CA1031 // A picker failure must not crash the wizard; leave the typed value in place.
        try
        {
            var folders = await top.StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions { AllowMultiple = false, Title = "Choose install location" });
            if (folders.Count > 0)
            {
                vm.InstallPath = folders[0].Path.LocalPath;
            }
        }
        catch (Exception)
        {
            // Best-effort: keep the current path.
        }
#pragma warning restore CA1031
    }
}
