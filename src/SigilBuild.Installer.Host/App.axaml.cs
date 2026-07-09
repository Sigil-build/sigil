using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SigilBuild.Installer.Host.Branding;
using SigilBuild.Installer.Host.ViewModels;
using SigilBuild.Installer.Host.Views;

namespace SigilBuild.Installer.Host;

public partial class App : Application
{
    private InstallerViewModel? _vm;

    /// <summary>
    /// The outcome chosen by the user during this session.
    /// Read by <see cref="Program.Main"/> after the Avalonia lifetime exits.
    /// </summary>
    public int OutcomeExitCode => (int)(_vm?.OutcomeCode ?? InstallerOutcomeCode.Completed);

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
            _vm = new InstallerViewModel(tokens);

            // Wire the wizard's Installing screen to the real step engine via the
            // shared InstallSession that Program built from argv. Left unwired in
            // dev/preview runs where no session was staged.
            var session = HostRuntime.Session;
            if (session is not null)
            {
                _vm.ConfigureInstallRunner((progress, ct) => session.RunInstallAsync(progress, ct));
            }

            desktop.MainWindow = new InstallerWindow
            {
                DataContext = _vm,
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
