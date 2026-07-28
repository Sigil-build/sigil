using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace SigilBuild.Installer.Host.Views;

/// <summary>
/// Modal confirmation shown before interrupting an active install.
/// Returns <c>true</c> when the user confirms cancellation, <c>false</c> otherwise.
/// </summary>
public sealed partial class CancelConfirmDialog : Window
{
    public CancelConfirmDialog()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnYes(object? _, RoutedEventArgs __) => Close(true);
    private void OnNo(object? _, RoutedEventArgs __) => Close(false);
}
