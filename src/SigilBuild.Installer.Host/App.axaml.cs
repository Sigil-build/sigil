using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SigilBuild.Installer.Host.Branding;
using SigilBuild.Installer.Host.ViewModels;
using SigilBuild.Installer.Host.Views;
using SigilBuild.Wrapper.Engine;

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
            // Brand data travels inside the WrapperBlob (decision 11): derived
            // light/dark palette + base64 logo/hero, no BrandTokens.g.json sidecar.
            var tokens = LoadBrandTokens();
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

    /// <summary>
    /// Build <see cref="BrandTokens"/> from the blob embedded in the stamped exe.
    /// Falls back to defaults for an un-stamped dev/preview run.
    /// </summary>
    private static BrandTokens LoadBrandTokens()
    {
        var brand = InstallerBrandLoader.LoadFromSelf();
        if (brand is null)
            return new BrandTokens();

        var light = brand.Light ?? new Dictionary<string, string>();
        var dark = brand.Dark ?? new Dictionary<string, string>();

        return new BrandTokens
        {
            AppName = brand.DisplayName ?? "Application",
            Publisher = brand.Publisher ?? "Publisher",
            AppVersion = brand.Version ?? "1.0.0",
            PrimaryColor = light.TryGetValue("railBg", out var railBg) ? railBg : "#1F2937",
            AccentColor = light.TryGetValue("accent", out var accent) ? accent : "#3B82F6",
            LightTokens = light,
            DarkTokens = dark,
            LogoBase64 = brand.LogoBase64,
            HeroBase64 = brand.HeroBase64,
        };
    }
}
