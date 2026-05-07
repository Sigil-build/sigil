using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SigilBuild.Installer.Host.Branding;
using SigilBuild.Installer.Host.ViewModels;
using SigilBuild.Installer.Host.Views;

namespace SigilBuild.Installer.Host;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            BrandTokens tokens;
            try
            {
                tokens = BrandTokens.LoadOrDefault("BrandTokens.g.json");
            }
            catch (System.Text.Json.JsonException)
            {
                tokens = new BrandTokens();
            }
            BrandPalette.Apply(this, tokens);
            desktop.MainWindow = new InstallerWindow
            {
                DataContext = new InstallerViewModel(tokens),
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
