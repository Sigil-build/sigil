using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SigilBuild.Installer.Host.Branding;
using SigilBuild.Installer.Host.ViewModels;
using SigilBuild.Installer.Host.Views;
using SigilBuild.Wrapper.Cli;
using SigilBuild.Wrapper.Engine;

namespace SigilBuild.Installer.Host;

public partial class App : Application
{
    private InstallerViewModel? _vm;
    private UninstallViewModel? _uninstallVm;

    /// <summary>
    /// The outcome chosen by the user during this session (install OR the T15
    /// interactive uninstall). Read by <see cref="Program.Main"/> after the Avalonia
    /// lifetime exits.
    /// </summary>
    public int OutcomeExitCode =>
        (int)(_vm?.OutcomeCode ?? _uninstallVm?.OutcomeCode ?? InstallerOutcomeCode.Completed);

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Brand data travels inside the WrapperBlob (decision 11): derived
            // light/dark palette + base64 logo/hero, no BrandTokens.g.json sidecar.
            var tokens = LoadBrandTokens();
            BrandPalette.Apply(this, tokens);

            var session = HostRuntime.Session;

            // T15: an interactive uninstall (uninstall.exe double-clicked, no /S)
            // gets its own minimal branded confirm → progress → done window, driving
            // the real UninstallEngine. Kept entirely separate from the install
            // wizard's flow/rail.
            if (session is not null && session.Mode == WrapperMode.Uninstall)
            {
                _uninstallVm = new UninstallViewModel(tokens);
                _uninstallVm.ConfigureRunner((progress, ct) =>
                    session.RunUninstallInteractiveAsync(progress, ct));
                desktop.MainWindow = new UninstallWindow { DataContext = _uninstallVm };
                base.OnFrameworkInitializationCompleted();
                return;
            }

            _vm = new InstallerViewModel(tokens);

            // Wire the wizard's Installing screen to the real step engine via the
            // shared InstallSession that Program built from argv. Left unwired in
            // dev/preview runs where no session was staged.
            if (session is not null)
            {
                // T9: load the declared custom screens (from the blob) + parameter
                // schema (from the session) so the wizard renders the Configure-style
                // forms and generates the rail from them.
                _vm.LoadScreens(InstallerScreensLoader.LoadFromSelf(), session.Parameters);

                // T8: load the enabled built-in option components (from the session's
                // blob). When ≥ 1 is present the Options screen + its rail entry appear
                // (after license, per decision 4); when none, they are omitted.
                _vm.LoadOptions(session.Options);

                // T14: load the embedded license text (from the blob). When present
                // the License screen + its rail entry appear (after destination, per
                // decision 4) and gate Next on acceptance; when absent they are
                // omitted. The /silent path never reaches here, so silent installs
                // imply acceptance.
                _vm.LoadLicense(InstallerLicenseLoader.LoadFromSelf());

                // Bind the wizard-collected parameter values into param.* and the
                // option checkbox states into option.* for the engine at install
                // time (read lazily at call time).
                _vm.ConfigureInstallRunner((progress, ct) =>
                    session.RunInstallAsync(
                        _vm.CollectedParameterValues, _vm.CollectedOptionValues, progress, ct));
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
