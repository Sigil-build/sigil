using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Cli;

namespace SigilBuild.Wrapper.Engine;

/// <summary>
/// Outcome of a single install run: success, or failure with the engine's
/// error message (the rollback has already completed by the time this is
/// returned). Cancellation is surfaced as a thrown
/// <see cref="OperationCanceledException"/>, not an <see cref="InstallOutcome"/>.
/// </summary>
public sealed record InstallOutcome(bool Success, string? Error);

/// <summary>
/// The single install/uninstall driver shared by both stamped entry points —
/// the console <c>SigilBuild.Wrapper</c> and the Avalonia
/// <c>SigilBuild.Installer.Host</c>. It loads the embedded <see cref="WrapperBlob"/>,
/// parses argv through the one <see cref="CommandLineParser"/>, and exposes:
/// <list type="bullet">
///   <item><description><see cref="RunHeadlessAsync"/> for <c>/silent</c> / <c>/verysilent</c> and non-interactive modes;</description></item>
///   <item><description><see cref="RunInstallAsync"/> for the GUI wizard's Installing screen (drives the same engine with an <see cref="IProgress{T}"/> adapter);</description></item>
/// </list>
/// On a successful install both paths run the same completion step (persist the
/// rollback journal + write the ARP entry) via <see cref="PersistCompletion"/>.
/// </summary>
public sealed class InstallSession
{
    private readonly WrapperBlob _blob;
    private readonly ParsedCommandLine _parsed;
    private readonly InstallScope _scope;
    private readonly WrapperMode _mode;
    private readonly UpgradePlan _plan;

    /// <summary>
    /// The dedicated non-zero exit code returned by the silent path when a newer
    /// version is already installed and <c>/force-downgrade</c> was not supplied
    /// (P3, gap G3). Distinct from 1 (step failure), 2 (cancelled), and 64 (usage).
    /// The wizard maps the same value onto <c>InstallerOutcomeCode.DowngradeBlocked</c>.
    /// </summary>
    public const int DowngradeBlockedExitCode = 3;

    private InstallSession(WrapperBlob blob, ParsedCommandLine parsed, InstallScope scope, UpgradePlan plan)
    {
        _blob = blob;
        _parsed = parsed;
        _scope = scope;
        _plan = plan;
        _mode = ResolveEffectiveMode(parsed.Mode);
    }

    /// <summary>
    /// Resolve the effective operating mode. The survivable <c>uninstall.exe</c>
    /// (T15) is a byte-for-byte copy of the setup exe, so double-clicking it — with
    /// no <c>/Uninstall</c> flag — must still uninstall. When no explicit mode flag
    /// was parsed and the running image is named <c>uninstall.exe</c>, imply
    /// <see cref="WrapperMode.Uninstall"/>. The ARP <c>UninstallString</c> path
    /// (<c>/S /Uninstall</c>) already parses to Uninstall and is unaffected.
    /// </summary>
    private static WrapperMode ResolveEffectiveMode(WrapperMode parsedMode)
    {
        if (parsedMode != WrapperMode.Install)
        {
            return parsedMode;
        }

        var exe = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exe) &&
            string.Equals(
                Path.GetFileName(exe),
                InstallSurvivability.UninstallerFileName,
                StringComparison.OrdinalIgnoreCase))
        {
            return WrapperMode.Uninstall;
        }

        return parsedMode;
    }

    /// <summary>
    /// Build a session for the running exe: read the embedded blob, parse
    /// <paramref name="args"/> against its parameter schema, and resolve the
    /// effective install scope (T12) from the manifest scope + <c>/allusers</c> /
    /// <c>/currentuser</c> flags.
    /// </summary>
    /// <exception cref="UsageException">
    /// Bad flag / undeclared parameter; a required parameter (no default) left
    /// unset in silent install mode; or a scope flag that conflicts with a fixed
    /// manifest scope (<c>/allusers</c> against <c>scope: user</c>, etc.).
    /// </exception>
    public static InstallSession Create(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var blob = WrapperBlob.LoadFromSelf();
        var parsed = CommandLineParser.Parse(args, blob.Parameters);
        return Build(blob, parsed, DefaultStateResolver);
    }

    // Registry-backed installed-state probe (P3). Off Windows there is no ARP, so the
    // run is always fresh. The un-stamped Empty blob is short-circuited in Build.
    private static UpgradeState DefaultStateResolver(string appId, InstallScope tentativeScope)
        => OperatingSystem.IsWindows()
            ? InstalledStateResolver.Resolve(appId, tentativeScope)
            : UpgradeState.None;

    /// <summary>
    /// Resolve the effective scope + version-aware plan (P3) from the blob, parsed CLI,
    /// and an installed-state probe, then construct the session. Shared by
    /// <see cref="Create"/> (real registry probe) and the test seams (injected state).
    /// </summary>
    private static InstallSession Build(
        WrapperBlob blob,
        ParsedCommandLine parsed,
        Func<string, InstallScope, UpgradeState> stateResolver)
    {
        var tentativeScope = ScopeResolver.Resolve(blob.Scope, parsed.Scope);
        var state = ReferenceEquals(blob, WrapperBlob.Empty)
            ? UpgradeState.None
            : stateResolver(blob.AppId, tentativeScope);

        // The existing install's scope wins over an AUTO-resolved scope (manifest
        // `scope: auto`, no explicit /allusers|/currentuser) so an upgrade re-targets
        // exactly what was installed. A fixed manifest scope or an explicit scope flag
        // is authoritative and is never overridden here.
        var effectiveScope = tentativeScope;
        if (state.Found
            && blob.Scope == InstallScope.Auto
            && parsed.Scope == ScopeOverride.None)
        {
            effectiveScope = state.FoundScope;
        }

        var plan = UpgradePlanner.Decide(state, blob.Version, parsed.ForceDowngrade);
        return new InstallSession(blob, parsed, effectiveScope, plan);
    }

    /// <summary>
    /// Build a session directly from an in-memory blob + parsed command line,
    /// bypassing <see cref="WrapperBlob.LoadFromSelf"/>. Test-only seam: lets a
    /// test drive the real install lifecycle (payload extraction → engine →
    /// temp cleanup) with a synthesised blob instead of a stamped exe. Resolves
    /// scope the same way <see cref="Create"/> does.
    /// </summary>
    internal static InstallSession ForTesting(WrapperBlob blob, ParsedCommandLine parsed)
    {
        ArgumentNullException.ThrowIfNull(blob);
        ArgumentNullException.ThrowIfNull(parsed);
        return Build(blob, parsed, static (_, _) => UpgradeState.None);
    }

    /// <summary>
    /// Test seam for the P3 version-aware paths: build a session with an INJECTED
    /// installed state (no real registry probe), so a test can drive the fresh /
    /// same / upgrade / downgrade decision and its scope-wins effect deterministically.
    /// </summary>
    internal static InstallSession ForTesting(WrapperBlob blob, ParsedCommandLine parsed, UpgradeState installedState)
    {
        ArgumentNullException.ThrowIfNull(blob);
        ArgumentNullException.ThrowIfNull(parsed);
        ArgumentNullException.ThrowIfNull(installedState);
        return Build(blob, parsed, (_, _) => installedState);
    }

    /// <summary>
    /// The effective operating mode (install / update / uninstall). Normally the
    /// parsed CLI mode; upgraded to <see cref="WrapperMode.Uninstall"/> when the
    /// running image is the copied <c>uninstall.exe</c> even without an explicit
    /// <c>/Uninstall</c> flag (T15 self-detection).
    /// </summary>
    public WrapperMode Mode => _mode;

    /// <summary>True when <c>/silent</c>, <c>/S</c>, or <c>/verysilent</c> was supplied.</summary>
    public bool Silent => _parsed.Silent;

    /// <summary>True when <c>/verysilent</c> was supplied.</summary>
    public bool VerySilent => _parsed.VerySilent;

    /// <summary>The install's application id (the ARP subkey / state-store key).</summary>
    public string AppId => _blob.AppId;

    /// <summary>The declared install-time parameter schema (for the wizard's Configure screens, Task T9).</summary>
    public IReadOnlyList<ParameterDefinition> Parameters => _blob.Parameters;

    /// <summary>
    /// The ENABLED built-in option components (T8) the wizard renders on the
    /// Options screen (one checkbox each; <c>locked</c> ones disabled). Empty when
    /// the manifest declared no options — the host then omits the Options screen.
    /// </summary>
    public IReadOnlyList<InstallerOptionComponent> Options =>
        _blob.Options ?? Array.Empty<InstallerOptionComponent>();

    /// <summary>The full parsed command line (overrides, install-dir, scope) for GUI defaults.</summary>
    public ParsedCommandLine CommandLine => _parsed;

    /// <summary>
    /// The effective install scope (T12) resolved from the manifest scope and the
    /// <c>/allusers</c> / <c>/currentuser</c> flags. Always
    /// <see cref="InstallScope.User"/> or <see cref="InstallScope.Machine"/>.
    /// </summary>
    public InstallScope ResolvedScope => _scope;

    /// <summary>
    /// True when this install needs an elevated relaunch: a per-machine scope was
    /// resolved but the current process is not elevated (T12). The entry point
    /// relaunches itself via <see cref="Elevation.RelaunchElevatedAndWait"/> and
    /// propagates the child's exit code. Per-user (and auto-user) installs are
    /// always <c>false</c>, so they stay prompt-free.
    /// </summary>
    public bool RequiresElevation =>
        _scope == InstallScope.Machine && !Elevation.IsProcessElevated();

    /// <summary>
    /// Run to completion without any UI. Routes by mode, echoing the engine's
    /// log lines to <paramref name="output"/>. Returns the process exit code:
    /// <c>0</c> ok, <c>1</c> step failure (rolled back), <c>2</c> cancelled
    /// (rolled back), <c>64</c> unsupported mode.
    /// </summary>
    public async Task<int> RunHeadlessAsync(TextWriter output, TextWriter error, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        switch (_mode)
        {
            case WrapperMode.Update:
                // Update is not implemented in this track. Say so explicitly and
                // exit 64 rather than silently running the (always-empty)
                // WrapperBlob.UpdateSteps and reporting success.
                error.WriteLine(
                    "/Update is not supported by this installer: update_steps run via the delta-update SDK, not the setup runtime.");
                return 64;

            case WrapperMode.Uninstall:
                return await RunUninstallAsync(error, ct).ConfigureAwait(false);

            case WrapperMode.Install:
            default:
                // P3: refuse a downgrade to an older version when a newer one is
                // installed and /force-downgrade was not supplied — dedicated exit code,
                // nothing installed (the journal is never opened).
                if (_plan.Action == UpgradeAction.DowngradeBlocked)
                {
                    error.WriteLine(DowngradeBlockedMessage());
                    return DowngradeBlockedExitCode;
                }

                var progress = new WriterProgress(output);
                try
                {
                    var outcome = await RunInstallAsync(progress, ct).ConfigureAwait(false);
                    if (outcome.Success)
                    {
                        return 0;
                    }
                    if (outcome.Error is not null)
                    {
                        error.WriteLine(outcome.Error);
                    }
                    return 1;
                }
                catch (OperationCanceledException)
                {
                    // Rollback already ran inside the engine before the throw.
                    return 2;
                }
        }
    }

    /// <summary>
    /// Run the install pipeline (pre → install → post) under a single rollback
    /// journal, forwarding <paramref name="progress"/> to the engine, and — on
    /// success — persist the journal + write the ARP entry. A step failure
    /// returns <c>Success=false</c> after the engine has rolled back;
    /// cancellation propagates as <see cref="OperationCanceledException"/> after
    /// the engine has rolled back.
    /// </summary>
    // Wizard-collected parameter values (T9), bound into the engine context on the
    // next RunInstall* call. Null on the console/silent path. Set once, before the
    // single install run — the host is a single-install process.
    private IReadOnlyDictionary<string, string>? _collectedValues;

    // Wizard-collected option checkbox states (T8), bound into `option.*` on the
    // next RunInstall* call. Null on the console/silent path (options then resolve
    // to their manifest defaults, subject to any CLI `/P<name>` override).
    private IReadOnlyDictionary<string, bool>? _collectedOptions;

    /// <summary>
    /// The wizard-collected destination path (T13), set by the host's Destination
    /// screen before the single install run. It takes precedence over <c>/D=</c>
    /// and the manifest <c>install_dir</c> when the effective install dir is
    /// resolved, so the <c>{install_dir}</c> token expands to what the user chose.
    /// Null on the console/silent path — <c>/D=</c> (or the default) then wins.
    /// </summary>
    public string? CollectedInstallDir { get; set; }

    /// <summary>
    /// Resolve the install directory the wizard's Destination screen should
    /// pre-fill (T13) for <paramref name="scope"/> (the current scope toggle
    /// selection, or <see cref="ResolvedScope"/> when the toggle is hidden). Honors
    /// a <c>/D=</c> override and the manifest <c>install_dir</c>, resolving their
    /// <c>{scope_root}</c> / <c>{app.*}</c> tokens; falls back to
    /// <c>&lt;scope root&gt;\&lt;App.Name&gt;</c>. Does NOT consider a previously
    /// collected path, so re-toggling scope recomputes a clean default.
    /// </summary>
    public string ResolveDefaultInstallDir(InstallScope? scope = null) =>
        InstallDirResolver.Resolve(
            scope: scope ?? _scope,
            appName: _blob.AppName,
            appId: _blob.AppId,
            manifestInstallDir: _blob.InstallDir,
            cliOverride: _parsed.InstallDir,
            collected: null,
            // P3: an upgrade pre-fills the prior install dir so the Destination screen
            // defaults to the existing location (preserving user data).
            priorInstallDir: PriorInstallDirDefault);

    /// <summary>
    /// True when the effective scope is <see cref="InstallScope.Auto"/>-derived and
    /// the manifest did not fix it — i.e. the wizard should show the user/machine
    /// scope toggle on the Destination screen (T12/T13). A manifest that fixes
    /// <c>scope: user</c> or <c>scope: machine</c> hides the toggle.
    /// </summary>
    public bool ScopeIsSelectable => _blob.Scope == InstallScope.Auto;

    /// <summary>
    /// True when a prior install of this <see cref="AppId"/> is already recorded in
    /// the resolved scope (T10 re-install / upgrade detection). The wizard surfaces a
    /// repair/reinstall notice (v1: uninstall-then-install), and every install path
    /// (silent and GUI) first replays the recorded uninstall so a second consecutive
    /// install re-lays each mutation exactly once — no duplicate PATH entries,
    /// shortcuts, or ARP rows. Always <c>false</c> off Windows and for the un-stamped
    /// <see cref="WrapperBlob.Empty"/> runtime.
    /// </summary>
    public bool ExistingInstallDetected
    {
        get
        {
            if (ReferenceEquals(_blob, WrapperBlob.Empty) || !OperatingSystem.IsWindows())
            {
                return false;
            }
            return UninstallStateStore.TryLoad(_blob.AppId, _scope) is not null;
        }
    }

    /// <summary>
    /// The version-aware classification for this run (P3, gap G3): fresh / same /
    /// upgrade / downgrade-blocked / downgrade-forced, resolved once at session start
    /// from the scope-correct ARP entry vs the packed version.
    /// </summary>
    public UpgradeAction UpgradeAction => _plan.Action;

    /// <summary>
    /// The version currently installed (the prior ARP <c>DisplayVersion</c>), or
    /// <c>""</c> when this is a fresh install. Surfaced by the wizard as
    /// "Upgrading from x.y.z" and in the downgrade-block notice / message.
    /// </summary>
    public string InstalledVersion => _plan.InstalledVersion;

    /// <summary>
    /// True when a newer version is installed and <c>/force-downgrade</c> was not
    /// supplied — the install is refused (silent: <see cref="DowngradeBlockedExitCode"/>;
    /// wizard: notice screen).
    /// </summary>
    public bool IsDowngradeBlocked => _plan.Action == UpgradeAction.DowngradeBlocked;

    /// <summary>
    /// The prior install directory to honor as the default destination during an
    /// upgrade / forced downgrade (P3) — the install lands in the existing location so
    /// non-journaled user data is preserved. <c>null</c> for fresh / same installs,
    /// and for a cross-scope re-install (the prior dir belongs to the OTHER scope, so
    /// the new scope's own default is used instead), where the normal T13 precedence applies.
    /// </summary>
    private string? PriorInstallDirDefault =>
        _plan.RemovesPriorVersion
        && _plan.FoundScope == _scope       // same-scope upgrade only; a scope change uses the new scope's default
        && !string.IsNullOrEmpty(_plan.PriorInstallDir)
            ? _plan.PriorInstallDir
            : null;

    private string DowngradeBlockedMessage()
    {
        var name = string.IsNullOrWhiteSpace(_blob.DisplayName) ? _blob.AppId : _blob.DisplayName;
        var target = string.IsNullOrWhiteSpace(_blob.Version) ? "this version" : _blob.Version!;
        return $"A newer version ({_plan.InstalledVersion}) of {name} is already installed. " +
               $"Installing the older version {target} is blocked. Pass /force-downgrade to override.";
    }

    public Task<InstallOutcome> RunInstallAsync(IProgress<StepProgress>? progress, CancellationToken ct = default)
        => RunInstallCoreAsync(WrapperBlob.LoadPayloadBytes(), progress, ct);

    /// <summary>
    /// GUI entry point (T9): run the install pipeline binding the wizard-collected
    /// parameter values into <c>param.*</c> / <c>parameters.*</c> for the engine,
    /// so a step <c>when: "param.autostart == true"</c> observes what the user
    /// picked on the custom screens. Values are keyed by canonical parameter name;
    /// they take precedence over CLI <c>/P</c> overrides and schema defaults.
    /// </summary>
    public Task<InstallOutcome> RunInstallAsync(
        IReadOnlyDictionary<string, string>? collectedValues,
        IProgress<StepProgress>? progress,
        CancellationToken ct = default)
    {
        _collectedValues = collectedValues;
        return RunInstallCoreAsync(WrapperBlob.LoadPayloadBytes(), progress, ct);
    }

    /// <summary>
    /// GUI entry point (T8 + T9): run the install pipeline binding both the
    /// wizard-collected parameter values (<c>param.*</c> / <c>parameters.*</c>) and
    /// the wizard-collected option checkbox states (<c>option.*</c>) into the engine
    /// context, so an auto-generated step gated on <c>when: option.desktop_shortcut</c>
    /// — or a hand-written step gated on an option — observes what the user picked on
    /// the Options screen. Collected option values take precedence over CLI
    /// <c>/P&lt;name&gt;</c> overrides and manifest defaults; a <c>locked</c> component
    /// stays fixed at its default regardless.
    /// </summary>
    public Task<InstallOutcome> RunInstallAsync(
        IReadOnlyDictionary<string, string>? collectedValues,
        IReadOnlyDictionary<string, bool>? collectedOptions,
        IProgress<StepProgress>? progress,
        CancellationToken ct = default)
    {
        _collectedValues = collectedValues;
        _collectedOptions = collectedOptions;
        return RunInstallCoreAsync(WrapperBlob.LoadPayloadBytes(), progress, ct);
    }

    /// <summary>
    /// Core install lifecycle shared by every entry path: extract the embedded
    /// payload into a fresh temp dir, run the pre → install → post pipeline with
    /// the payload root threaded into the <see cref="StepContext"/> (so
    /// <c>payload://</c> sources resolve), and — on success — persist the journal
    /// and register ARP. The temp dir is removed in the <c>finally</c>,
    /// guaranteeing no <c>%TEMP%</c> leak on success, step failure,
    /// cancellation, or rollback.
    /// </summary>
    /// <remarks>
    /// <paramref name="payloadBytes"/> is passed in (rather than read here) so a
    /// test can exercise the full lifecycle — including cleanup — without a
    /// stamped exe; the public <see cref="RunInstallAsync"/> supplies the real
    /// <see cref="WrapperBlob.LoadPayloadBytes"/> result. An empty array (the
    /// un-stamped runtime) skips extraction and leaves the payload root
    /// <c>null</c>.
    /// </remarks>
    internal async Task<InstallOutcome> RunInstallCoreAsync(
        byte[] payloadBytes,
        IProgress<StepProgress>? progress,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payloadBytes);

        // P3 downgrade guard (defense-in-depth): the headless path already exits with
        // DowngradeBlockedExitCode and the wizard routes to a notice screen instead of
        // calling this — but never run a blocked downgrade if something reaches here.
        if (_plan.Action == UpgradeAction.DowngradeBlocked)
        {
            return new InstallOutcome(false, DowngradeBlockedMessage());
        }

        // P3 version-aware pre-body phase (gap G3). For an UPGRADE (older installed) or a
        // FORCED DOWNGRADE (newer installed + /force-downgrade), remove the prior version
        // by running ITS uninstall.exe /S /Uninstall and require exit 0 — BEFORE opening
        // the rollback journal or extracting the payload, so a failure here leaves NO
        // partial install. Otherwise fall through to the T10 re-install cleanup: a no-op
        // for a fresh install, and the unchanged uninstall-then-install repair path for
        // the SAME version (replays the recorded uninstall so the reinstall re-lays each
        // mutation exactly once — no duplicate PATH entries, shortcuts, or ARP rows).
        if (_plan.RemovesPriorVersion)
        {
            var pre = await RunPriorUninstallAsync(progress, ct).ConfigureAwait(false);
            if (!pre.Success)
            {
                return pre; // clear failure; journal never opened, no partial install.
            }
        }
        else
        {
            await PerformReinstallCleanupAsync(ct).ConfigureAwait(false);
        }

        PayloadExtraction? payload = null;
        try
        {
            string? payloadRoot = null;
            if (payloadBytes.Length > 0)
            {
                payload = PayloadExtraction.Extract(payloadBytes, _blob.AppId);
                payloadRoot = payload.Root;
            }

            var ctx = StepContext.From(
                _blob, _parsed, payloadRoot, _collectedValues, _scope, _collectedOptions, CollectedInstallDir,
                // P3: an upgrade installs into the prior location (default destination).
                priorInstallDir: PriorInstallDirDefault);
            var result = await new InstallEngine().RunAsync(
                preInstall: _blob.PreInstall,
                installSteps: _blob.InstallSteps,
                postInstall: _blob.PostInstall,
                ctx: ctx,
                ct: ct,
                progress: progress).ConfigureAwait(false);

            if (!result.Success)
            {
                return new InstallOutcome(false, result.Error);
            }

            PersistCompletion(result.Journal, ctx.SecretValues, ctx.InstallDir);
            // Install committed: a rollback can no longer be requested, so the
            // transient file_delete / directory_delete stashes (%TEMP%\sigil-fd-* /
            // sigil-dd-*) are dead weight. Reclaim them so a successful install
            // never leaves %TEMP% residue (they are not part of the persisted
            // uninstall journal, so discarding them changes no post-install state).
            result.Journal.DiscardTransientStashes();
            return new InstallOutcome(true, null);
        }
        finally
        {
            // Runs on success, step-failure return, and the OperationCanceledException
            // rethrow — so the extracted payload temp dir never leaks.
            payload?.Dispose();
        }
    }

    /// <summary>
    /// T10 re-install / upgrade cleanup: when a prior install of this AppId is
    /// recorded in the resolved scope, drive <see cref="UninstallEngine"/> to replay
    /// its persisted journal in reverse (restoring the prior PATH, deleting the prior
    /// shortcut / files, removing the prior ARP row + state) before the fresh install
    /// re-applies everything. Because the earlier PATH append is undone first, the
    /// reinstall appends the install dir exactly once — the double-install case no
    /// longer duplicates PATH entries, shortcuts, or ARP rows. A best-effort step: a
    /// failed prior-uninstall (e.g. missing state) must not block the reinstall, so
    /// the outcome is intentionally ignored. No-op for the un-stamped runtime and
    /// off Windows.
    /// </summary>
    private async Task PerformReinstallCleanupAsync(CancellationToken ct)
    {
        if (!ExistingInstallDetected)
        {
            return;
        }
        // UndoAsync + ARP.Remove + state delete. Progress is suppressed — the
        // reinstall's own progress stream begins with the fresh install below.
        await new UninstallEngine()
            .RunAsync(_blob.AppId, _scope, progress: null, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// P3 upgrade / forced-downgrade pre-body phase: run the PRIOR version's own
    /// <c>uninstall.exe /S /Uninstall &lt;scope&gt;</c> and require exit code 0. The prior
    /// uninstaller (not this build's <see cref="UninstallEngine"/>) is used because it owns
    /// the prior version's rollback journal and knows how to reverse it. Runs before the
    /// journal is opened or the payload extracted, so any failure — a missing uninstaller,
    /// a spawn error, or a non-zero exit — aborts the run with NO partial install. Non-journaled
    /// user data is untouched (uninstall only reverses what the prior install journaled).
    /// Cancellation propagates as <see cref="OperationCanceledException"/>.
    /// </summary>
    private async Task<InstallOutcome> RunPriorUninstallAsync(IProgress<StepProgress>? progress, CancellationToken ct)
    {
        var exe = _plan.PriorUninstallExe;
        var verb = _plan.Action == UpgradeAction.DowngradeForced ? "Removing newer version" : "Removing previous version";
        // (0, 1) keeps the wizard progress bar at 0 (Total 0 would read as 1.0/complete)
        // while the message shows; the real install's engine progress supersedes it.
        progress?.Report(new StepProgress(0, 1, $"{verb} {_plan.InstalledVersion}…", false));

        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
        {
            return new InstallOutcome(
                false,
                $"cannot upgrade: the previous version's uninstaller was not found at '{exe}'. No changes were made.");
        }

        // Remove the prior version in ITS OWN scope (where it physically lives), which
        // can differ from the new effective scope on a cross-scope re-install (e.g.
        // /allusers over a per-user install). Keying the flag off _scope would tell the
        // per-user uninstaller to look in the machine hive and fail.
        var scopeFlag = _plan.FoundScope == InstallScope.Machine ? "/allusers" : "/currentuser";
        int exitCode;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("/S");
            psi.ArgumentList.Add("/Uninstall");
            psi.ArgumentList.Add(scopeFlag);

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null)
            {
                return new InstallOutcome(
                    false,
                    $"cannot upgrade: could not start the previous version's uninstaller '{exe}'. No changes were made.");
            }

            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            exitCode = proc.ExitCode;
        }
#pragma warning disable CA1031 // Surface any spawn/wait failure as a typed outcome; the install has not started.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new InstallOutcome(
                false,
                $"cannot upgrade: removing the previous version failed to run ({ex.Message}). No changes were made.");
        }
#pragma warning restore CA1031

        if (exitCode != 0)
        {
            return new InstallOutcome(
                false,
                $"cannot upgrade: removing the previous version failed (uninstaller exit code {exitCode}). No changes were made.");
        }

        return new InstallOutcome(true, null);
    }

    /// <summary>
    /// Shared install-completion helper: on a successful install, snapshot the
    /// rollback journal to the state store and register the ARP entry so the
    /// app appears in Add/Remove Programs. Used by both the console and GUI
    /// paths.
    /// </summary>
    /// <remarks>
    /// The DisplayName/Version/Publisher/size values are the real
    /// <c>manifest.App.*</c> fields and the packed size, threaded through the blob
    /// at pack time (T10). The ARP hive, state-store location, and
    /// uninstall-string scope flag all follow the resolved scope (T12). The
    /// <c>UninstallString</c> points at the copied <c>uninstall.exe</c> (T15), never
    /// at <see cref="Environment.ProcessPath"/> — so uninstall survives deletion of
    /// the downloaded setup exe. No-ops for an un-stamped runtime (the dev/smoke
    /// <see cref="WrapperBlob.Empty"/>) and off Windows.
    /// </remarks>
    /// <param name="resolvedInstallDir">
    /// The SINGLE install directory T13's <see cref="InstallDirResolver"/> computed
    /// for this run (<see cref="StepContext.InstallDir"/>) — the exact directory the
    /// steps installed into. <c>uninstall.exe</c> is copied here and the ARP
    /// <c>UninstallString</c> targets it, so the uninstaller can never diverge from
    /// where the files landed (honoring <c>/D=</c> / manifest / wizard / default).
    /// <c>null</c> only for a context built without a resolved dir (the un-stamped
    /// runtime, which already returned early above); it then falls back to the legacy
    /// <c>&lt;scope root&gt;\&lt;AppId&gt;</c> so completion never crashes.
    /// </param>
    private void PersistCompletion(
        RollbackJournal journal,
        IReadOnlyList<string> secretValues,
        string? resolvedInstallDir)
    {
        if (ReferenceEquals(_blob, WrapperBlob.Empty))
        {
            return; // un-stamped runtime: nothing real to register.
        }
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Unify on the single resolved install dir (T13): uninstall.exe is copied
        // into the SAME directory the steps installed to, so ARP's UninstallString
        // never diverges from the files. Fall back to the legacy scope-root + AppId
        // location only if the resolved dir is somehow absent (never on a real
        // stamped install — Empty already returned early above).
        var uninstallDir = string.IsNullOrEmpty(resolvedInstallDir)
            ? Path.Combine(ScopeLayout.For(_scope).InstallRoot, _blob.AppId)
            : resolvedInstallDir;

        // T15 final install step: copy the running installer into the install dir
        // as uninstall.exe and journal its removal (so the persisted journal — and a
        // rollback — reverse it). ARP's UninstallString then targets this copy.
        var uninstallerPath =
            InstallSurvivability.InstallUninstaller(journal, uninstallDir)
            ?? Environment.ProcessPath
            ?? ".";

        // Redact any secret value from the persisted uninstall state (decision 6).
        // The scope is recorded so uninstall runs in the same scope (T12). Saved
        // AFTER the uninstaller-copy step is journaled so the RemoveUninstaller
        // record is part of the persisted, replay-on-uninstall journal.
        UninstallStateStore.Save(_blob.AppId, journal, _scope, secretValues);
        // T10: register the REAL manifest.App.* fields + packed size threaded through
        // the blob, not the former AppId / "1.0.0" / "Unknown" / 0 placeholders. The
        // fallbacks only fire for a (theoretical) blob that omitted them — a real
        // packed blob always carries them.
        ArpRegistration.Register(new ArpRegistration.Entry(
            AppId: _blob.AppId,
            DisplayName: string.IsNullOrWhiteSpace(_blob.DisplayName) ? _blob.AppId : _blob.DisplayName,
            DisplayVersion: string.IsNullOrWhiteSpace(_blob.Version) ? "0.0.0" : _blob.Version,
            Publisher: string.IsNullOrWhiteSpace(_blob.Publisher) ? "Unknown" : _blob.Publisher,
            UninstallString: ArpRegistration.BuildUninstallString(uninstallerPath, _scope),
            EstimatedSizeBytes: _blob.EstimatedSizeBytes,
            // P3: write InstallLocation so a later upgrade can recover the install dir.
            InstallLocation: uninstallDir),
            _scope);
    }

    private async Task<int> RunUninstallAsync(TextWriter error, CancellationToken ct)
    {
        var result = await new UninstallEngine()
            .RunAsync(_blob.AppId, _scope, progress: null, ct)
            .ConfigureAwait(false);
        if (!result.Success)
        {
            if (result.Error is not null)
            {
                error.WriteLine(result.Error);
            }
            return 1;
        }
        return 0;
    }

    /// <summary>
    /// GUI entry point for the interactive uninstall flow (T15): drive
    /// <see cref="UninstallEngine"/> for this session's app, forwarding
    /// <paramref name="progress"/> so the wizard's uninstall progress screen can
    /// grow its reversal log (<c>unlink</c> / <c>path -</c> / <c>reg -</c> /
    /// <c>delete</c>). Returns an <see cref="InstallOutcome"/> so the uninstall
    /// view-model shares the same success/failure shape as the install flow. The
    /// headless <c>/S /Uninstall</c> path continues to use
    /// <see cref="RunHeadlessAsync"/>.
    /// </summary>
    public async Task<InstallOutcome> RunUninstallInteractiveAsync(
        IProgress<StepProgress>? progress, CancellationToken ct = default)
    {
        var result = await new UninstallEngine()
            .RunAsync(_blob.AppId, _scope, progress, ct)
            .ConfigureAwait(false);
        return new InstallOutcome(result.Success, result.Error);
    }

    /// <summary>
    /// Synchronous <see cref="IProgress{T}"/> that echoes each non-null log line
    /// to a <see cref="TextWriter"/> — used by the headless path so console
    /// output stays ordered (unlike <see cref="Progress{T}"/>, which marshals to
    /// a captured <see cref="SynchronizationContext"/>).
    /// </summary>
    private sealed class WriterProgress : IProgress<StepProgress>
    {
        private readonly TextWriter _writer;

        public WriterProgress(TextWriter writer) => _writer = writer;

        public void Report(StepProgress value)
        {
            if (value.Message is not null)
            {
                _writer.WriteLine(value.Message);
            }
        }
    }
}
