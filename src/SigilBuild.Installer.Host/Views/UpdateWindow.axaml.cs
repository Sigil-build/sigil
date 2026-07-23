using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using SigilBuild.Installer.Host.ViewModels;

namespace SigilBuild.Installer.Host.Views;

/// <summary>
/// The headed, non-silent <c>/Update</c> window (T12.4): a minimal branded
/// progress → up-to-date/done/failed flow, separate from the install
/// <see cref="InstallerWindow"/> and the <see cref="UninstallWindow"/>. State-driven
/// panels are toggled by the view-model's <c>IsProgress</c>/<c>IsUpToDate</c>/
/// <c>IsDone</c>/<c>IsFailed</c> flags. Unlike <see cref="UninstallWindow"/> there is
/// no confirm gesture — the view-model starts checking as soon as it is constructed
/// (see <c>App.axaml.cs</c>), so this code-behind only wires the terminal Close button.
/// </summary>
public partial class UpdateWindow : Window
{
    public UpdateWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnClose(object? _, RoutedEventArgs __) => Close();
}
