using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SigilBuild.Core.Manifest;
using SigilBuild.Installer.Host.Branding;
using SigilBuild.Installer.Host.ViewModels;
using SigilBuild.Installer.Host.Views;
using SigilBuild.Wrapper.Cli;
using SigilBuild.Wrapper.Core.Localization;
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

                // T14 / P9 Step 3b: load the embedded license text MAP (from the blob)
                // and resolve it against the SAME ordered preference list the chrome
                // language used (session.LanguagePreferences), so a manifest packing
                // uk: LICENSE.uk.txt actually renders Ukrainian under a Ukrainian
                // session instead of English forever. Resolve is total here — SIG0290
                // (Task 9) makes an en-less license map a fatal pack-time error, so
                // this never silently returns null for a non-null map. When present
                // the License screen + its rail entry appear (after destination, per
                // decision 4) and gate Next on acceptance; when absent they are
                // omitted. The /silent path never reaches here, so silent installs
                // imply acceptance.
                var licenseMap = InstallerLicenseLoader.LoadMapFromSelf();
                _vm.LoadLicense(InstallerLicenseLoader.Resolve(licenseMap, session.LanguagePreferences));

                // T10: surface the reinstall notice when the SAME version is already
                // installed (the engine performs uninstall-then-install itself). An
                // upgrade / downgrade is a P3 case handled by SetUpgradeState below, so
                // the plain reinstall notice is suppressed for those.
                _vm.SetExistingInstall(
                    session.ExistingInstallDetected && session.UpgradeAction == UpgradeAction.Same);

                // P3 (gap G3): tell the wizard whether this run is an upgrade — show the
                // "Upgrading from x.y.z" banner — or a blocked downgrade, which routes
                // straight to the notice screen with the dedicated exit code. The prior
                // install dir is already honored by ResolveDefaultInstallDir below.
                _vm.SetUpgradeState(session.UpgradeAction, session.InstalledVersion);

                // T13: seed the Destination screen from the session — the scope-aware
                // default install dir (honoring /D= + the manifest install_dir), and
                // whether the user/machine scope toggle shows (manifest `scope: auto`).
                // Toggling scope recomputes the default path for the picked scope.
                _vm.ConfigureDestination(
                    session.ScopeIsSelectable,
                    isMachine => session.ResolveDefaultInstallDir(
                        isMachine ? InstallScope.Machine : InstallScope.User),
                    session.ResolveDefaultInstallDir());

                // Bind the wizard-collected parameter values into param.* and the
                // option checkbox states into option.* for the engine at install
                // time (read lazily at call time). The collected destination path
                // (T13) becomes the effective install dir → {install_dir}.
                _vm.ConfigureInstallRunner(async (progress, ct) =>
                {
                    session.CollectedInstallDir = _vm.InstallPath;
                    var outcome = await session.RunInstallAsync(
                        _vm.CollectedParameterValues, _vm.CollectedOptionValues, progress, ct);
                    // P5: the reboot flag is only known after the run — copy it into the
                    // VM before the Done screen renders (StartInstallAsync reads outcome next).
                    _vm.SetRebootRequired(session.RebootRequired);
                    return outcome;
                });

                // P7: surface the /LOG path so the Failed screen can offer "Open log".
                _vm.LogFilePath = session.LogFilePath;

                // P6 (gap G7): wire the files-in-use probes so the wizard can gate on
                // running applications before starting the engine, and offer to close
                // them via the Restart Manager.
                _vm.ConfigureBlockerProbe(
                    scan: dir =>
                    {
                        var blockers = session.ScanBlockers(dir);
                        var described = new List<string>(blockers.Count);
                        foreach (var b in blockers)
                        {
                            described.Add(b.Describe());
                        }
                        return described;
                    },
                    close: dir => session.CloseBlockers(dir));

                // P2 (gap G4): wire the Done-screen "Launch <App>" checkbox to the
                // session's unelevated launch of installer.run_after_install.
                _vm.ConfigureLaunch(
                    session.HasRunAfterInstall,
                    session.LaunchLabel,
                    () => session.LaunchAppUnelevated());
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
            AppName = brand.DisplayName ?? Strings.BrandAppFallback(SessionLanguage.Current),
            Publisher = brand.Publisher ?? Strings.BrandPublisherFallback(SessionLanguage.Current),
            AppVersion = brand.Version ?? "1.0.0",
            PrimaryColor = light.TryGetValue("railBg", out var railBg) ? railBg : "#1F2937",
            AccentColor = light.TryGetValue("accent", out var accent) ? accent : "#3B82F6",
            LightTokens = light,
            DarkTokens = dark,
            LogoBase64 = brand.LogoBase64,
            HeroBase64 = brand.HeroBase64,
            // T11 / decision 7: the "Signed by {publisher}" trust line renders ONLY
            // when the manifest declared a `sign` block (SignDeclared, from the blob)
            // AND this exe's own Authenticode signature verifies via WinVerifyTrust.
            // InstallerTrustLoader short-circuits the P/Invoke when SignDeclared is
            // false, so an unsigned/un-stamped host does no trust work and shows no
            // line; a signed-then-tampered/re-stamped exe fails verification and also
            // shows no line. The neutral publisher name renders separately regardless.
            TrustLine = InstallerTrustLoader.ResolveFromSelf(),
        };
    }
}
