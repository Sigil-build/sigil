using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using SigilBuild.Installer.Host.ViewModels;

namespace SigilBuild.Installer.Host.Views.Screens;

/// <summary>
/// Dedicated Install Directory page (NSIS convention). Surfaces a Browse...
/// button that opens a folder picker and shows the destination drive's free
/// space below the path TextBox so the user can spot insufficient-space
/// problems before the install runs.
/// </summary>
public partial class InstallDirView : UserControl
{
    public InstallDirView()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not InstallerViewModel vm) return;
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null) return;
            var folder = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select install location",
                AllowMultiple = false,
            });
            if (folder.Count == 0) return;
            var path = folder[0].TryGetLocalPath();
            if (!string.IsNullOrEmpty(path)) vm.InstallPath = path;
        }
#pragma warning disable CA1031 // top-level UI handler — must not crash the wizard
        catch (Exception ex)
        {
            InstallerLog.Error("InstallDirView.OnBrowse threw", ex);
        }
#pragma warning restore CA1031
    }
}
