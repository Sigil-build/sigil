// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using System.Collections.Generic;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
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
    private UpdateViewModel? _updateVm;

    /// <summary>
    /// The outcome chosen by the user during this session (install, the
    /// interactive uninstall, or a headed <c>/Update</c>). Read by
    /// <see cref="Program.Main"/> after the Avalonia lifetime exits. The headed
    /// <c>/Update</c> run surfaces UpdateRunner's own int exit code directly (it is
    /// not one of the fixed <see cref="InstallerOutcomeCode"/> values — e.g. a
    /// launched child can return 3010), so it takes precedence when present.
    /// </summary>
    public int OutcomeExitCode =>
        _updateVm?.OutcomeExitCode
        ?? (int)(_vm?.OutcomeCode ?? _uninstallVm?.OutcomeCode ?? InstallerOutcomeCode.Completed);

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Brand data travels inside the WrapperBlob: derived light/dark palette
            // + base64 logo/hero, no BrandTokens.g.json sidecar.
            var tokens = LoadBrandTokens();
            BrandPalette.Apply(this, tokens);

            // Kick the Authenticode/revocation lookup onto a thread-pool thread.
            // Started here, before any window is constructed, so the trust line is
            // already on its way by the time the wizard paints, and the paint never
            // waits for it. (R48)
            StartTrustLineResolution(tokens);

            var session = HostRuntime.Session;

            // An interactive uninstall (uninstall.exe double-clicked, no /S)
            // gets its own minimal branded confirm → [close apps] → progress → done
            // window, driving the real UninstallEngine. Kept entirely separate from
            // the install wizard's flow/rail.
            if (session is not null && session.Mode == WrapperMode.Uninstall)
            {
                _uninstallVm = new UninstallViewModel(tokens);
                // The session runs pre_uninstall hooks, then the files-in-use gate,
                // and returns a blocked outcome before any replay; the view-model shows
                // the Close-applications step and invokes the runner again to resume. (R103)
                _uninstallVm.ConfigureRunner((progress, ct) =>
                    session.RunUninstallInteractiveAsync(progress, ct));
                // The same probes the install wizard wires below, over the directory
                // the uninstall's own gate sweeps. (R103)
                _uninstallVm.ConfigureBlockerProbe(
                    scan: () => DescribeBlockers(session.ScanUninstallBlockers()),
                    close: () => session.CloseUninstallBlockers());
                // Nothing to remove — a leftover uninstall.exe: open on the neutral
                // not-installed screen rather than offering an uninstall that can only
                // fail. A refused state is not "not installed" and still fails. (R1, R107)
                if (session.IsNotInstalled())
                {
                    InstallerLog.Info($"uninstall: {session.AppId} is not installed in this scope; nothing to remove");
                    _uninstallVm.ShowNotInstalled();
                }
                desktop.MainWindow = new UninstallWindow { DataContext = _uninstallVm };
                base.OnFrameworkInitializationCompleted();
                return;
            }

            // A non-silent /Update (Program.cs routes it here instead of the
            // headless path) gets its own minimal branded progress → up-to-date/done/
            // failed window, driving the real InstallSession.RunUpdateInteractiveAsync
            // — which runs the SAME UpdateRunner as the headless path, but launches
            // the downloaded child Setup.exe HEADED (no /silent) so the user sees its
            // own install wizard. Kept entirely separate from the install wizard's
            // flow/rail, mirroring the uninstall window above. Unlike Uninstall
            // there is no confirm gesture — Start() begins checking immediately.
            if (session is not null && session.Mode == WrapperMode.Update)
            {
                _updateVm = new UpdateViewModel(tokens);
                _updateVm.ConfigureRunner((report, ct) =>
                    session.RunUpdateInteractiveAsync(report, ct));
                _updateVm.Start();
                desktop.MainWindow = new UpdateWindow { DataContext = _updateVm };
                base.OnFrameworkInitializationCompleted();
                return;
            }

            _vm = new InstallerViewModel(tokens);

            // Wire the wizard's Installing screen to the real step engine via the
            // shared InstallSession that Program built from argv. Left unwired in
            // dev/preview runs where no session was staged.
            if (session is not null)
            {
                // Load the declared custom screens (from the blob) + parameter
                // schema (from the session) so the wizard renders the Configure-style
                // forms and generates the rail from them.
                // Design §4.4: thread the session's full language-preference list
                // (the SAME list used below for the license map) so a declared screen's
                // rail label resolves independently of the resolved chrome language —
                // Sigil may ship no chrome catalog for a tag the manifest itself supplies.
                _vm.LoadScreens(InstallerScreensLoader.LoadFromSelf(), session.Parameters, session.LanguagePreferences);

                // Load the enabled built-in option components (from the session's
                // blob). When ≥ 1 is present the Options screen + its rail entry appear
                // (after license); when none, they are omitted.
                _vm.LoadOptions(session.Options);

                // Load the embedded license text MAP (from the blob) and resolve it
                // against the SAME ordered preference list the chrome language used
                // (session.LanguagePreferences), so a manifest packing
                // uk: LICENSE.uk.txt actually renders Ukrainian under a Ukrainian
                // session instead of English forever. Resolve is total here — SIG0290
                // makes an en-less license map a fatal pack-time error, so this never
                // silently returns null for a non-null map. When present the License
                // screen + its rail entry appear (after destination) and gate Next on
                // acceptance; when absent they are omitted. The /silent path never
                // reaches here, so silent installs imply acceptance.
                var licenseMap = InstallerLicenseLoader.LoadMapFromSelf();
                _vm.LoadLicense(InstallerLicenseLoader.Resolve(licenseMap, session.LanguagePreferences));

                // Surface the reinstall notice when the SAME version is already
                // installed (the engine performs uninstall-then-install itself). An
                // upgrade / downgrade is handled by SetUpgradeState below, so the
                // plain reinstall notice is suppressed for those.
                _vm.SetExistingInstall(
                    session.ExistingInstallDetected && session.UpgradeAction == UpgradeAction.Same);

                // Tell the wizard whether this run is an upgrade — show the
                // "Upgrading from x.y.z" banner — or a blocked downgrade, which routes
                // straight to the notice screen with the dedicated exit code. The prior
                // install dir is already honored by ResolveDefaultInstallDir below.
                _vm.SetUpgradeState(session.UpgradeAction, session.InstalledVersion);

                // Seed the Destination screen from the session — the scope-aware
                // default install dir (honoring /D= + the manifest install_dir), and
                // whether the user/machine scope toggle shows (manifest `scope: auto`).
                // Toggling scope recomputes the default path for the picked scope.
                _vm.ConfigureDestination(
                    session.ScopeIsSelectable,
                    isMachine => session.ResolveDefaultInstallDir(
                        isMachine ? InstallScope.Machine : InstallScope.User),
                    session.ResolveDefaultInstallDir());

                // Picking "All users of this computer" cannot be honoured by a session
                // whose scope already resolved to `user` — scope selects the
                // containment roots and the elevation decision, both fixed before this
                // window existed. Wired only when that is the case; a session already
                // in machine scope toggles the radio normally. (R90)
                if (session.MachineScopeNeedsRelaunch && OperatingSystem.IsWindows())
                {
                    // The inner platform check is not redundant to the analyzer: it
                    // cannot carry the guard above across the lambda boundary, and the
                    // handoff is Windows-only (ShellExecuteExW "runas").
                    _vm.ConfigureMachineScopeElevation(() =>
                        OperatingSystem.IsWindows() && TryHandOffToElevatedInstance(session, desktop));
                }

                // Bind the wizard-collected parameter values into param.* and the
                // option checkbox states into option.* for the engine at install
                // time. The collected destination path becomes the effective install
                // dir → {install_dir}. The runner executes on a thread-pool thread, so
                // it reads the request the VM captured on the UI thread and never the
                // VM itself. (R95)
                var vm = _vm;
                vm.ConfigureInstallRunner(async (request, progress, ct) =>
                {
                    session.CollectedInstallDir = request.InstallPath;
                    var outcome = await session.RunInstallAsync(
                        request.ParameterValues, request.OptionValues, progress, ct).ConfigureAwait(false);
                    // The reboot flag is only known after the run. Set it on the UI
                    // thread, and before this runner completes, so the Done screen
                    // StartInstallAsync shows next already carries it.
                    var rebootRequired = session.RebootRequired;
                    await Dispatcher.UIThread.InvokeAsync(() => vm.SetRebootRequired(rebootRequired));
                    return outcome;
                });

                // The manifest's pre_install hooks run before the wizard's own
                // files-in-use scan, so an app's own --quit gets its chance before
                // the Close-applications screen appears. Wired only when the phase
                // exists. The session memoizes the outcome, so the install run that
                // follows does not run the hooks a second time. (R93)
                if (session.HasPreInstallHooks)
                {
                    vm.ConfigurePreInstallHooks((request, ct) =>
                    {
                        session.CollectedInstallDir = request.InstallPath;
                        return session.RunPreInstallHooksAsync(
                            request.ParameterValues, request.OptionValues, progress: null, ct);
                    });
                }

                // Surface the /LOG path so the Failed screen can offer "Open log".
                _vm.LogFilePath = session.LogFilePath;

                // Wire the files-in-use probes so the wizard can gate on running
                // applications before starting the engine, and offer to close them
                // via the Restart Manager.
                _vm.ConfigureBlockerProbe(
                    scan: dir => DescribeBlockers(session.ScanBlockers(dir)),
                    close: dir => session.CloseBlockers(dir));

                // Wire the Done-screen "Launch <App>" checkbox to the session's
                // unelevated launch of installer.run_after_install.
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
    /// Hand the install to a fresh elevated instance running in machine scope, and
    /// shut this one down. Returns <c>false</c> when no elevated instance was
    /// started — the UAC prompt was declined, the shell refused, or the relaunch
    /// vector could not be built — in which case this process keeps the run and the
    /// wizard reverts the selection. (R90)
    /// </summary>
    /// <remarks>
    /// Deliberately does not wait for the child. The call happens with a window on
    /// screen, and blocking the UI thread for the length of an elevated install would
    /// leave a frozen window sitting behind it. It also could not tell a declined
    /// prompt from a child the user cancelled — both surface as the same exit code —
    /// and that distinction is the only thing the caller acts on.
    /// <para>
    /// This process then exits <c>0</c>, which reports that it did its own job — hand
    /// off — and not that anything was installed. The elevated instance owns the
    /// install and reports its own outcome to the user. Nothing reads this code in
    /// practice: the path is reachable only by clicking a radio button, and a script
    /// selects scope with <c>/allusers</c> instead.
    /// </para>
    /// </remarks>
    [SupportedOSPlatform("windows")]
    private static bool TryHandOffToElevatedInstance(
        InstallSession session, IClassicDesktopStyleApplicationLifetime desktop)
    {
        IReadOnlyList<string> relaunchArgs;
        try
        {
            relaunchArgs = session.BuildMachineScopeRelaunchArgs(HostRuntime.Args);
        }
        catch (UsageException ex)
        {
            // The secret handoff envelope could not be written. Refusing to relaunch
            // is the point — the alternative is elevating with a parameter value on
            // the command line, where every process-creation auditor sees it. (R18)
            InstallerLog.Error("machine-scope relaunch refused", ex);
            return false;
        }

        if (!Elevation.TryRelaunchElevated(relaunchArgs))
        {
            InstallerLog.Info("machine scope declined: no elevated instance was started");
            return false;
        }

        InstallerLog.Info("machine scope chosen: handed off to an elevated instance");
        desktop.Shutdown();
        return true;
    }

    /// <summary>The Close-applications list's lines: one description (name + pid) per blocker.</summary>
    private static List<string> DescribeBlockers(IReadOnlyList<AppBlocker> blockers)
    {
        var described = new List<string>(blockers.Count);
        foreach (var b in blockers)
        {
            described.Add(b.Describe());
        }
        return described;
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
            // The "Signed by {publisher}" trust line renders ONLY when the manifest
            // declared a `sign` block (SignDeclared, from the blob) AND this exe's own
            // Authenticode signature verifies via WinVerifyTrust.
            // InstallerTrustLoader short-circuits the P/Invoke when SignDeclared is
            // false, so an unsigned/un-stamped host does no trust work and shows no
            // line; a signed-then-tampered/re-stamped exe fails verification and also
            // shows no line. The neutral publisher name renders separately regardless.
            //
            // NOT resolved here. That call is WinVerifyTrust with whole-chain
            // revocation checking — a network operation, measured at 335 ms on the happy
            // path — and this runs while the first window is being constructed, so the
            // wizard would not paint until it returned. TrustLine is left null (which
            // renders as no line, the safe default) and filled in from a thread-pool
            // thread by StartTrustLineResolution. (R48)
        };
    }

    /// <summary>
    /// Resolve the trust line off the UI thread and let the binding fill it in. (R48)
    /// </summary>
    /// <remarks>
    /// Fire-and-forget on purpose: the wizard must not wait for it, and the failure mode
    /// is the same as an unsigned artifact — no line. The trust line is additive
    /// assurance, so its momentary absence claims nothing.
    /// </remarks>
    private static void StartTrustLineResolution(BrandTokens tokens) =>
        _ = TrustLineActivation.BeginAsync(
            tokens,
            InstallerTrustLoader.ResolveFromSelf,
            action => Dispatcher.UIThread.Post(action));
}
