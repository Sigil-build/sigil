using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Cli;
using SigilBuild.Wrapper.Core.Localization;
using SigilBuild.Wrapper.Update;

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

    // P7: the install log for this run, opened lazily the first time a run path
    // executes when /LOG (or /LOG=path) was supplied. Null when logging was not
    // requested or the target could not be created (best-effort).
    private InstallLog? _log;

    // P9 (gap G10): recorded by ResolveSessionLanguage when the manifest's fixed
    // installer.language pin overrides a conflicting /lang flag. Flushed as a log
    // line by EnsureLog the first time the /LOG sink opens (see the design note on
    // ResolveSessionLanguage for why this is "logged", not fatal).
    private string? _languageConflictNote;

    /// <summary>
    /// The dedicated non-zero exit code returned by the silent path when a newer
    /// version is already installed and <c>/force-downgrade</c> was not supplied
    /// (P3, gap G3). Distinct from 1 (step failure), 2 (cancelled), and 64 (usage).
    /// The wizard maps the same value onto <c>InstallerOutcomeCode.DowngradeBlocked</c>.
    /// </summary>
    public const int DowngradeBlockedExitCode = 3;

    // P5: set when a prerequisite installer returned exit code 3010 during this run.
    // Surfaced on the Done screen and as the silent success exit code 3010.
    private bool _rebootRequired;

    /// <summary>
    /// True when a prerequisite (P5, gap G6) installed during this run reported
    /// reboot-required (exit code 3010). The wizard's Done screen shows a reboot
    /// notice and the silent path returns exit code 3010 (success-but-reboot). Only
    /// meaningful after a successful <see cref="RunInstallAsync(IProgress{StepProgress}, CancellationToken)"/>.
    /// </summary>
    public bool RebootRequired => _rebootRequired;

    /// <summary>The dedicated silent exit code for a successful install that needs a reboot (P5).</summary>
    public const int RebootRequiredExitCode = 3010;

    /// <summary>
    /// The dedicated non-zero exit code returned by the silent path when running
    /// applications hold the install directory open and <c>/closeapps</c> was not
    /// supplied (P6, gap G7). The log names each blocker. Distinct from 1 (step
    /// failure), 2 (cancelled), 3 (downgrade blocked), and 64 (usage).
    /// </summary>
    public const int FilesInUseExitCode = 4;

    /// <summary>
    /// The dedicated non-zero exit code returned when another setup instance for this
    /// app+scope is already running (P6, gap G17). The first instance is unaffected.
    /// </summary>
    public const int AlreadyRunningExitCode = 5;

    /// <summary>
    /// <c>/Update</c> (P12, T12.3): the installer is not update-enabled — the manifest
    /// declared no <c>updates.manifestUrl</c>, so there is nothing to check. Distinct
    /// from 64 (which stays reserved for a genuinely-malformed invocation).
    /// </summary>
    public const int UpdateNotConfiguredExitCode = 6;

    /// <summary>
    /// <c>/Update</c> (P12, T12.3): "could not check for updates / could not apply".
    /// A network failure fetching the channel manifest or its signature, a malformed
    /// channel manifest (SIG0320), an implausible package checksum, or a failed
    /// package download / child spawn. An operational failure — nothing was changed.
    /// </summary>
    public const int UpdateCheckFailedExitCode = 7;

    /// <summary>
    /// <c>/Update</c> (P12, T12.3): the channel manifest's detached signature did not
    /// verify against <c>updates.signingKey</c> (SIG0321). A HARD security reject — a
    /// tampered or unsigned channel manifest is never acted on. Kept distinct from
    /// <see cref="UpdateCheckFailedExitCode"/> so a tampering event is unambiguous.
    /// </summary>
    public const int UpdateManifestRejectedExitCode = 8;

    /// <summary>
    /// <c>/Update</c> (P12, T12.3): a newer version is advertised but the installed
    /// version is below the channel manifest's <c>minFromVersion</c> floor, so it
    /// cannot update via this path. Distinct from "up to date" (exit 0) — an update
    /// exists, it just cannot be taken from the current version.
    /// </summary>
    public const int UpdateNotEligibleExitCode = 9;

    // P6: set when the files-in-use gate refused the run, so the headless path can
    // map the generic failure onto FilesInUseExitCode.
    private bool _blockedByFilesInUse;

    private InstallSession(WrapperBlob blob, ParsedCommandLine parsed, InstallScope scope, UpgradePlan plan)
    {
        _blob = blob;
        _parsed = parsed;
        _scope = scope;
        _plan = plan;
        _mode = ResolveEffectiveMode(parsed.Mode);
    }

    /// <summary>
    /// The resolved <c>/LOG</c> file path for this run (P7), or <c>null</c> when
    /// logging was not requested. Explicit <c>/LOG=path</c> wins; bare <c>/LOG</c>
    /// resolves to <c>%TEMP%\sigil-&lt;appid&gt;.log</c>. Exposed so the wizard's
    /// Failed screen can offer to open the log.
    /// </summary>
    public string? LogFilePath => _parsed.LogRequested ? ResolveLogPath() : null;

    private string ResolveLogPath() =>
        _parsed.LogPath ?? Path.Combine(Path.GetTempPath(), $"sigil-{SanitizeAppId(_blob.AppId)}.log");

    // Reduce the AppId to a safe file-name segment for the default log path
    // (AppId can be "<unset>" for the dev runtime, or a reverse-DNS id).
    private static string SanitizeAppId(string appId)
    {
        var sb = new System.Text.StringBuilder(appId.Length);
        foreach (var c in appId)
        {
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_');
        }
        var s = sb.ToString();
        return s.Length == 0 ? "app" : s;
    }

    /// <summary>
    /// Open the <c>/LOG</c> sink (idempotent) and write the header line. No-op when
    /// logging was not requested or already opened. Returns the log (or null).
    /// </summary>
    private InstallLog? EnsureLog()
    {
        if (!_parsed.LogRequested)
        {
            return null;
        }
        if (_log is not null)
        {
            return _log;
        }

        _log = InstallLog.TryOpen(ResolveLogPath());
        _log?.WriteLine(
            $"=== sigil {_mode.ToString().ToLowerInvariant()} log | app={_blob.AppId} | scope={_scope} | args=[{_parsed.AuditSafeRendering()}] ===");
        if (_languageConflictNote is not null)
        {
            _log?.WriteLine(_languageConflictNote);
        }
        return _log;
    }

    /// <summary>
    /// A log-only progress sink (R1). The state store reports its provenance
    /// decisions — a refused machine-scope load, a repaired state-directory DACL —
    /// on an <see cref="IProgress{T}"/>, so every call site that has no user-facing
    /// progress stream of its own still has to hand it something or the refusal is
    /// written to nowhere. Mirrors the existing sink built at
    /// <see cref="RunUninstallAsync"/>: nothing reaches the console or the wizard,
    /// but the <c>/LOG</c> file records it. <c>null</c> when <c>/LOG</c> was not
    /// requested, in which case there is no sink to write to at all.
    /// </summary>
    private IProgress<StepProgress>? StateProgress =>
        _log is null ? null : new LoggingProgress(null, _log);

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
        var parsed = CommandLineParser.Parse(args, blob.Parameters, CustomOptionNames(blob));
        return Build(blob, parsed, DefaultStateResolver);
    }

    /// <summary>
    /// P10 (gap G11): write a log line for each locked component that a CLI
    /// <c>/P</c> override tried to change — the override is ignored (the component
    /// stays at its default), so the author gets told rather than silently confused.
    /// The override key is the bare <c>&lt;name&gt;</c> for a built-in and the
    /// namespaced <c>option.&lt;name&gt;</c> for a custom component (mirroring how the
    /// CLI parser stores it).
    /// </summary>
    private void WarnIgnoredLockedOverrides()
    {
        if (_blob.Options is null || _parsed.Options.Count == 0)
        {
            return;
        }

        foreach (var opt in _blob.Options)
        {
            if (!opt.Locked)
            {
                continue;
            }

            var cliKey = opt.Custom ? "option." + opt.Name : opt.Name;
            if (_parsed.Options.ContainsKey(cliKey))
            {
                _log?.WriteLine(
                    $"option '{opt.Name}': locked — ignoring override attempt (stays at default {opt.Default})");
            }
        }
    }

    /// <summary>
    /// P10 (gap G11): the declared app-defined custom component names, handed to the
    /// CLI parser so the namespaced <c>/Poption.&lt;name&gt;=value</c> form validates
    /// against the closed set. <c>null</c> when the blob declares no custom component.
    /// </summary>
    private static List<string>? CustomOptionNames(WrapperBlob blob)
    {
        if (blob.Options is null)
        {
            return null;
        }

        var names = new List<string>();
        foreach (var opt in blob.Options)
        {
            if (opt.Custom)
            {
                names.Add(opt.Name);
            }
        }
        return names.Count == 0 ? null : names;
    }

    /// <summary>
    /// P9 (gap G10): resolve this session's chrome language — <c>installer.language</c>
    /// (fixed, wins) -&gt; <c>/lang</c> -&gt; the OS UI-language preference list -&gt;
    /// <c>en</c> — and set <see cref="SessionLanguage.Current"/>. Deliberately NOT
    /// called from <see cref="Create"/>: both stamped entry points call this
    /// exactly once, immediately after <c>Create</c> succeeds and BEFORE any UI is
    /// constructed (including the pre-Avalonia single-instance <c>MessageBoxW</c>,
    /// itself a catalog string) — the resolver depends only on the blob and Win32,
    /// never on Avalonia, so this ordering is available. Kept out of <c>Create</c>
    /// so the hundreds of engine tests that call it never mutate the process-wide
    /// <see cref="SessionLanguage"/> singleton as a side effect.
    /// </summary>
    /// <remarks>
    /// Design §2.1: a fixed manifest language beats a conflicting <c>/lang</c> —
    /// the flag is IGNORED, not fatal (exit code stays 0). This deliberately does
    /// NOT mirror T12's fixed-scope-vs-<c>/allusers</c> rule (exit 64): scope is a
    /// trust/consequence boundary, language is a display preference, and failing
    /// an install over a cosmetic preference would be hostile. The conflict, if
    /// any, is recorded on <see cref="LanguageConflictNote"/> and flushed into the
    /// <c>/LOG</c> sink the first time it opens (<see cref="EnsureLog"/>); the
    /// entry point may also log it immediately through its own channel.
    /// </remarks>
    public Lang ResolveSessionLanguage()
    {
        // P9: SessionLanguage.OnUninitializedRead is the Release-mode safety net
        // fired when something reads .Current before Set below has run (a
        // startup-ordering bug — exactly what Task 13 hit). It is otherwise DEAD:
        // nothing subscribes. Wire it here, at session start, to the SAME /LOG
        // sink this session owns (EnsureLog) — the one place that owns both the
        // log and the session-language singleton — so the fallback stops being
        // silent. Idempotent: re-wiring on every call is harmless (no test/run
        // constructs more than one session concurrently), and this keeps the
        // hook alive for the lifetime of the static SessionLanguage type without
        // a separate bootstrap step.
        SessionLanguage.OnUninitializedRead = () => EnsureLog()?.WriteLine(
            "language: SessionLanguage.Current read before resolution — falling back to en (startup-ordering bug)");

        var manifestLanguage = InstallerLanguageLoader.LoadFromSelf();
        var preferences = LanguageResolver.Preferences(manifestLanguage, _parsed.Lang, OsUiLanguage.Preferences());
        _languagePreferences = preferences;
        var chrome = LanguageResolver.MatchChrome(preferences);
        SessionLanguage.Set(chrome);

        if (manifestLanguage is not null && _parsed.Lang is not null
            && !string.Equals(manifestLanguage, _parsed.Lang, StringComparison.OrdinalIgnoreCase))
        {
            _languageConflictNote =
                $"language: manifest pin '{manifestLanguage}' overrides /lang={_parsed.Lang}";
        }

        return chrome;
    }

    // P9 (Step 3b): the ordered preference list ResolveSessionLanguage resolved
    // the chrome language from — installer.language (fixed) -> /lang -> OS
    // preferences -> en. Stashed so the license map (which is NOT part of the
    // generated chrome catalog and therefore can't go through MatchChrome) is
    // resolved against the EXACT SAME list, never a freshly recomputed one.
    private IReadOnlyList<string>? _languagePreferences;

    /// <summary>
    /// The ordered language-preference list <see cref="ResolveSessionLanguage"/>
    /// resolved (installer.language fixed -&gt; /lang -&gt; OS preferences -&gt; en).
    /// Empty until <see cref="ResolveSessionLanguage"/> has run. Exposed so the
    /// host can resolve the embedded license map (Step 3b) against the SAME list
    /// the chrome language used, rather than recomputing it and risking drift.
    /// </summary>
    public IReadOnlyList<string> LanguagePreferences => _languagePreferences ?? Array.Empty<string>();

    /// <summary>
    /// The language-conflict note recorded by <see cref="ResolveSessionLanguage"/>
    /// (a fixed manifest language overriding a conflicting <c>/lang</c>), or
    /// <c>null</c> when none occurred. Also flushed into the <c>/LOG</c> sink by
    /// <see cref="EnsureLog"/>; exposed so each entry point can additionally log
    /// it through its own channel (e.g. the wizard's always-on diagnostic log).
    /// </summary>
    public string? LanguageConflictNote => _languageConflictNote;

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
    /// (rolled back), <c>3010</c> success but a prerequisite needs a reboot (P5),
    /// <c>64</c> unsupported mode.
    /// </summary>
    public async Task<int> RunHeadlessAsync(TextWriter output, TextWriter error, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        // P7: open the /LOG sink (header) before any work, so even an early exit
        // (e.g. /Update → 64) still produces a log, and write the final exit code
        // as the last line.
        EnsureLog();
        var code = await RunHeadlessCoreAsync(output, error, ct).ConfigureAwait(false);
        _log?.WriteLine($"exit code: {code}");
        return code;
    }

    private async Task<int> RunHeadlessCoreAsync(TextWriter output, TextWriter error, CancellationToken ct)
    {
        switch (_mode)
        {
            case WrapperMode.Update:
                return await RunUpdateAsync(output, error, ct).ConfigureAwait(false);

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
                        // P2 (gap G4): a silent install starts the run_after_install
                        // target only when /launch was given (default-on is a wizard
                        // affordance, not a silent one). Launched unelevated.
                        if (_parsed.Launch)
                        {
                            LaunchAppUnelevated();
                        }
                        // P5: success-but-reboot-required → dedicated exit code 3010.
                        return _rebootRequired ? RebootRequiredExitCode : 0;
                    }
                    if (outcome.Error is not null)
                    {
                        error.WriteLine(outcome.Error);
                    }
                    // P6 (gap G7): running apps held the install dir and /closeapps was
                    // not supplied — dedicated exit code, nothing was changed.
                    return _blockedByFilesInUse ? FilesInUseExitCode : 1;
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
            // R1: pass the log sink. Without it a refused machine-scope load would
            // make this property answer false with no trace anywhere — literally
            // "no prior install", which is the reading the brief forbids.
            return UninstallStateStore.TryLoad(_blob.AppId, _scope, StateProgress) is not null;
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

    // P9 design D2: NOT migrated to the catalog. This feeds the console/silent path
    // (RunHeadlessAsync's stderr) — the headless twin of the wizard's localized
    // DowngradeBlocked notice screen (InstallerViewModel.cs, which correctly stays on
    // Strings.DowngradeBody). It names the English CLI flag /force-downgrade, the same
    // reason BuildBlockerMessage above stays English: a console/silent message that
    // tells the operator which literal flag to pass must not be translated out from
    // under them.
    private string DowngradeBlockedMessage()
    {
        var name = string.IsNullOrWhiteSpace(_blob.DisplayName) ? _blob.AppId : _blob.DisplayName!;
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

        // P7: ensure the /LOG sink is open (idempotent — the headless path may have
        // opened it already; the GUI path opens it here).
        EnsureLog();

        // P10 (gap G11): a locked component is always applied at its default, so a
        // CLI override for it is silently ineffective — surface that in the log
        // rather than letting the author wonder why /P had no effect.
        WarnIgnoredLockedOverrides();

        // P3 downgrade guard (defense-in-depth): the headless path already exits with
        // DowngradeBlockedExitCode and the wizard routes to a notice screen instead of
        // calling this — but never run a blocked downgrade if something reaches here.
        if (_plan.Action == UpgradeAction.DowngradeBlocked)
        {
            return new InstallOutcome(false, DowngradeBlockedMessage());
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

            // P7: hand the run's secrets to the log for redaction, and tee the
            // engine's progress (step + rollback lines) into the /LOG file.
            _log?.SetSecrets(ctx.SecretValues);
            var effectiveProgress = _log is null ? progress : new LoggingProgress(progress, _log);

            // P6 (gap G7): files-in-use gate. The destination is now resolved, so scan
            // the declared app mutexes + Restart Manager over it. Runs FIRST — before
            // prerequisites download anything, before the prior version is torn down,
            // and before the journal opens — so a blocked run changes nothing.
            var blocked = CheckFilesInUse(ctx, ctx.InstallDir, effectiveProgress);
            if (blocked is not null)
            {
                return blocked;
            }

            // P5: prerequisites (detect → install → re-detect) run OUTSIDE and BEFORE
            // the pre_install hooks AND before the journal opens — and BEFORE the T10
            // re-install cleanup below, so a prerequisite failure aborts with the prior
            // install still intact (no data loss). An accepted 3010 sets the session
            // reboot flag (Done-screen notice + silent exit 3010). Prereqs are never journaled.
            var prereq = await PrerequisiteRunner.RunAsync(
                _blob.Prerequisites, ctx, _scope, effectiveProgress, ct).ConfigureAwait(false);
            if (!prereq.Success)
            {
                _log?.WriteLine($"result: aborted before install — {ctx.Redact(prereq.Error ?? "prerequisite failed")}");
                return new InstallOutcome(false, prereq.Error);
            }
            _rebootRequired = prereq.RebootRequired;

            // Prior-version teardown. Ordering reconciles P3 and P5: it runs AFTER
            // prerequisites succeed (P5 — a failed prereq must never tear down a
            // working prior install) and BEFORE the journal opens (P3 — a teardown
            // failure must leave NO partial install). Two shapes:
            //  • P3 UPGRADE (older installed) or FORCED DOWNGRADE — remove the prior
            //    version by running ITS uninstall.exe /S /Uninstall, requiring exit 0.
            //  • Otherwise — the unchanged T10 re-install cleanup: a no-op for a fresh
            //    install, and the uninstall-then-install repair path for the SAME
            //    version (replays the recorded uninstall so the reinstall re-lays each
            //    mutation exactly once — no duplicate PATH entries, shortcuts, ARP rows).
            if (_plan.RemovesPriorVersion)
            {
                var priorRemoval = await RunPriorUninstallAsync(effectiveProgress, ct).ConfigureAwait(false);
                if (!priorRemoval.Success)
                {
                    _log?.WriteLine($"result: aborted before install — {ctx.Redact(priorRemoval.Error ?? "prior uninstall failed")}");
                    return priorRemoval; // journal never opened, no partial install.
                }
            }
            else
            {
                await PerformReinstallCleanupAsync(ctx.InstallDir, ct).ConfigureAwait(false);
            }

            // P2: pre_install hooks run OUTSIDE and BEFORE the journal opens. A hook
            // that fails (default on_failure: fail) aborts here — the InstallEngine,
            // and therefore the rollback journal, never runs.
            var preHook = await HookRunner.RunAsync(
                "pre_install", _blob.HookPreInstall, ctx, effectiveProgress, ct).ConfigureAwait(false);
            if (!preHook.Success)
            {
                var err = $"pre_install hook '{preHook.FailedStepId}' failed: {preHook.Error}";
                _log?.WriteLine($"result: aborted before install — {ctx.Redact(err)}");
                return new InstallOutcome(false, err);
            }

            EngineResult result;
            try
            {
                result = await new InstallEngine().RunAsync(
                    preInstall: _blob.PreInstall,
                    installSteps: _blob.InstallSteps,
                    postInstall: _blob.PostInstall,
                    ctx: ctx,
                    ct: ct,
                    progress: effectiveProgress).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _log?.WriteLine("result: cancelled (rolled back)");
                throw;
            }

            if (!result.Success)
            {
                _log?.WriteLine($"result: failed — {ctx.Redact(result.Error ?? "unknown error")}");
                return new InstallOutcome(false, result.Error);
            }

            // P12 (T12.5): a web-installer stub is a pure delegating trampoline —
            // its own "install" is just http_download + run_program of the full
            // package, which ALREADY ran its own complete PersistCompletion (ARP
            // register, uninstall.exe copy, UninstallStateStore.Save) for this
            // SAME AppId/scope by the time run_program returns. Persisting AGAIN
            // here would clobber the child's real uninstall.json/uninstall.exe
            // with the stub's own trivial two-step journal, leaving an ARP row
            // that can never actually uninstall the app. Skip ONLY this
            // success-path bookkeeping call — the steps above still ran (and any
            // in-flight rollback on a step FAILURE still works normally via the
            // journal); this is the one call site PersistCompletion has.
            if (!_blob.IsDelegatingStub)
            {
                // R1: PersistCompletion runs AFTER every filesystem/registry mutation
                // has committed and after the uninstaller copy, but BEFORE the ARP
                // registration. An exception escaping here — e.g. a machine state
                // directory an unprivileged user pre-created whose DACL cannot be
                // repaired — would leave a fully installed app with no ARP row and no
                // uninstall state: unremovable, and triggerable by any unprivileged
                // user. Route it through the normal failure path instead.
#pragma warning disable CA1031 // A completion failure must become a typed install failure, never an escape.
                try
                {
                    PersistCompletion(result.Journal, ctx.SecretValues, ctx.InstallDir);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var err =
                        "the install completed but could not be registered for uninstall " +
                        $"({ex.Message}); rolling it back so nothing is left behind";
                    _log?.WriteLine($"result: failed — {ctx.Redact(err)}");
                    Report(effectiveProgress, ctx, $"error: {err}", isError: true);
                    // Best-effort by construction: UndoAsync swallows individual record
                    // failures, so only cancellation can escape it. InProcess (R1): this
                    // is the journal this run just built, not one read back from disk.
                    await result.Journal
                        .UndoAsync(ReplayAnchorage.InProcess, effectiveProgress, ct)
                        .ConfigureAwait(false);
                    return new InstallOutcome(false, err);
                }
#pragma warning restore CA1031
            }
            // Install committed: a rollback can no longer be requested, so the
            // transient file_delete / directory_delete stashes (%TEMP%\sigil-fd-* /
            // sigil-dd-*) are dead weight. Reclaim them so a successful install
            // never leaves %TEMP% residue (they are not part of the persisted
            // uninstall journal, so discarding them changes no post-install state).
            result.Journal.DiscardTransientStashes();

            // P2: post_install hooks run OUTSIDE and AFTER the journal commits,
            // before the Done screen. The install is already committed, so a hook
            // failure is never rolled back — it is logged (P7); on_failure only
            // controls whether the remaining post hooks in the phase still run.
            await HookRunner.RunAsync(
                "post_install", _blob.HookPostInstall, ctx, effectiveProgress, ct).ConfigureAwait(false);

            _log?.WriteLine("result: success");
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
    private async Task PerformReinstallCleanupAsync(string? resolvedInstallDir, CancellationToken ct)
    {
        if (!ExistingInstallDetected)
        {
            return;
        }
        // UndoAsync + ARP.Remove + state delete. User-facing progress is suppressed —
        // the reinstall's own progress stream begins with the fresh install below —
        // but the log-only sink is still passed so an R1 refusal (which would make
        // this cleanup silently do nothing) is recorded in the /LOG file.
        //
        // This is only the FALLBACK anchor (R1 clause (c)): the prior install recorded
        // where it actually landed, and UninstallEngine prefers that. This value —
        // the destination THIS run resolved — is used only for state written before
        // the recorded field existed.
        var fallback = string.IsNullOrWhiteSpace(resolvedInstallDir)
            ? Path.Combine(ScopeLayout.For(_scope).InstallRoot, _blob.AppId)
            : resolvedInstallDir;

        await new UninstallEngine()
            .RunAsync(_blob.AppId, fallback, _scope, StateProgress, ct)
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
        var verb = _plan.Action == UpgradeAction.DowngradeForced
            ? Strings.EngineRemovingNewer(SessionLanguage.Current)
            : Strings.EngineRemovingPrevious(SessionLanguage.Current);
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
        // StateProgress carries the R1 hardening trail (a repaired state-directory
        // DACL) into the /LOG file; without a sink the repair would be invisible.
        // uninstallDir is recorded in the state so the uninstall anchors its replay to
        // where the files ACTUALLY landed (R1 clause (c)). Recomputing a default at
        // uninstall time would refuse every file record of a /D= or wizard-chosen
        // install and leave the app unremovable.
        UninstallStateStore.Save(
            _blob.AppId, journal, _scope, secretValues, StateProgress, uninstallDir);
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

    /// <summary>
    /// P12 (T12.3): the headless <c>/Update</c> flow. Reads the <c>updates:</c>
    /// metadata threaded into the blob, then hands off to <see cref="UpdateRunner"/>
    /// with the production I/O seams (HTTP fetch over the shared client, P4 verified
    /// download, a real child-process launch) and a scope-correct installed-version
    /// probe (P3 <see cref="InstalledStateResolver"/>). Every stage is logged into the
    /// already-open <c>/LOG</c> sink and echoed to the console; the runner returns the
    /// process exit code (see the <c>Update*ExitCode</c> constants, or the child
    /// installer's own code when a newer version is installed).
    /// </summary>
    private async Task<int> RunUpdateAsync(TextWriter output, TextWriter error, CancellationToken ct)
    {
        void Report(string message, bool isError)
        {
            (isError ? error : output).WriteLine(message);
            _log?.WriteLine(message);
        }

        var runner = BuildUpdateRunner(Report);
        // T12.3 (unchanged): the headless path launches the downloaded child
        // Setup.exe /silent, forwarding only the scope.
        var request = BuildUpdateRequest(silentChild: true);
        return await runner.RunAsync(request, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// GUI entry point (T12.4): a HEADED, non-silent <c>/Update</c> run. Drives the
    /// SAME <see cref="UpdateRunner"/> decision logic as the headless
    /// <see cref="RunUpdateAsync"/> — nothing is duplicated — but reports each stage
    /// through a UI-bound callback instead of a <see cref="TextWriter"/>, and
    /// launches the downloaded child Setup.exe HEADED (no <c>/silent</c>, gap G-Update)
    /// so the user sees the new version's own install wizard, unlike the headless
    /// path's silent child. Mirrors <see cref="RunUninstallInteractiveAsync"/>'s shape
    /// for the headed uninstall flow. Returns the SAME exit code the headless path
    /// would return for an equivalent run (0, an Update*ExitCode constant, or the
    /// launched child's own exit code).
    /// </summary>
    public async Task<int> RunUpdateInteractiveAsync(Action<string, bool> report, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report);

        // P7: the headed /Update run logs the same stage trail as the headless path.
        EnsureLog();

        void Report(string message, bool isError)
        {
            report(message, isError);
            _log?.WriteLine(message);
        }

        var runner = BuildUpdateRunner(Report);
        var request = BuildUpdateRequest(silentChild: false);
        var code = await runner.RunAsync(request, ct).ConfigureAwait(false);
        _log?.WriteLine($"exit code: {code}");
        return code;
    }

    /// <summary>
    /// Wire the production I/O seams (HTTP fetch over the shared client, P4
    /// verified download, a real child-process launch) and a scope-correct
    /// installed-version probe (P3 <see cref="InstalledStateResolver"/>) into a
    /// fresh <see cref="UpdateRunner"/> reporting through <paramref name="report"/>.
    /// Shared by the headless and headed <c>/Update</c> entry points so neither
    /// duplicates this wiring.
    /// </summary>
    private UpdateRunner BuildUpdateRunner(Action<string, bool> report) =>
        new(
            fetcher: new HttpUpdateResourceFetcher(TimeSpan.FromSeconds(60)),
            downloader: new SigilPackageDownloader(TimeSpan.FromMinutes(30), maxAttempts: 3, report),
            launcher: new ProcessChildInstallerLauncher(),
            // P3: read the installed version from the scope-correct ARP entry. Off
            // Windows there is no ARP, so nothing is installed and any channel version
            // reads as newer (the same short-circuit the install path uses).
            installedStateProbe: () => OperatingSystem.IsWindows()
                ? InstalledStateResolver.Resolve(_blob.AppId, _scope)
                : UpgradeState.None,
            report: report);

    /// <summary>
    /// Build the <see cref="UpdateRequest"/> for this session's blob + scope, with
    /// <paramref name="silentChild"/> threaded through (T12.4) rather than hard-coded —
    /// <c>true</c> for the headless path (T12.3, unchanged), <c>false</c> for the
    /// headed path so the launched child shows its own wizard.
    /// </summary>
    private UpdateRequest BuildUpdateRequest(bool silentChild) =>
        new(
            ManifestUrl: _blob.UpdateManifestUrl,
            SigningKey: _blob.UpdateSigningKey,
            Channel: _blob.UpdateChannel,
            Scope: _scope,
            AppId: _blob.AppId,
            TempDirectory: Path.GetTempPath(),
            SilentChild: silentChild);

    private async Task<int> RunUninstallAsync(TextWriter error, CancellationToken ct)
    {
        // P7: uninstall.exe honors /LOG too — tee the reversal trail into the log.
        var progress = _log is null ? null : new LoggingProgress(null, _log);
        var ctx = BuildUninstallContext();

        // P6 (gap G7): uninstall.exe inherits the same parser, so it honors the same
        // gate — a running app would block the journal replay from deleting its files.
        // /closeapps closes them; otherwise refuse before anything is removed.
        var blocked = CheckFilesInUse(ctx, ctx.InstallDir, progress);
        if (blocked is not null)
        {
            error.WriteLine(blocked.Error);
            return FilesInUseExitCode;
        }

        // P2: pre_uninstall hooks run BEFORE the journal replays. A failure (default
        // on_failure: fail) aborts the uninstall.
        var preHook = await HookRunner.RunAsync(
            "pre_uninstall", _blob.HookPreUninstall, ctx, progress, ct).ConfigureAwait(false);
        if (!preHook.Success)
        {
            var msg = $"pre_uninstall hook '{preHook.FailedStepId}' failed: {preHook.Error}";
            _log?.WriteLine($"result: uninstall aborted — {ctx.Redact(msg)}");
            error.WriteLine(msg);
            return 1;
        }

        // R1 clause (c): ctx.InstallDir is resolved from the signed blob / manifest /
        // command line and anchors the replay of the persisted journal.
        var result = await new UninstallEngine()
            .RunAsync(_blob.AppId, UninstallAnchorFallback(ctx), _scope, progress, ct)
            .ConfigureAwait(false);
        if (!result.Success)
        {
            _log?.WriteLine($"result: uninstall failed — {result.Error}");
            if (result.Error is not null)
            {
                error.WriteLine(result.Error);
            }
            return 1;
        }

        // P2: post_uninstall hooks run AFTER the journal replays (best-effort
        // cleanup; failures are logged but never fail the completed uninstall).
        await HookRunner.RunAsync(
            "post_uninstall", _blob.HookPostUninstall, ctx, progress, ct).ConfigureAwait(false);

        _log?.WriteLine("result: uninstall success");
        return 0;
    }

    /// <summary>
    /// Build a <see cref="StepContext"/> for uninstall-time hook token resolution
    /// (<c>{var.*}</c> / <c>{install_dir}</c>). No wizard-collected values or payload
    /// apply at uninstall; the install dir resolves to the manifest / CLI / default.
    /// </summary>
    private StepContext BuildUninstallContext() =>
        StepContext.From(_blob, _parsed, payloadRoot: null, collected: null, scope: _scope);

    /// <summary>
    /// The FALLBACK anchor for a persisted-journal replay (R1 clause (c)) — used only
    /// when the state file predates the recorded-install-dir field, since
    /// <see cref="UninstallEngine"/> prefers the recorded value.
    /// </summary>
    /// <remarks>
    /// <see cref="BuildUninstallContext"/> resolves <c>InstallDir</c> from the manifest
    /// and command line with no collected value and no prior dir, so it is the DEFAULT
    /// destination, not necessarily the one the install used — the ARP
    /// <c>UninstallString</c> carries no <c>/D=</c>. That is precisely why it must not
    /// be the primary anchor. The directory holding the running <c>uninstall.exe</c> is
    /// a better guess where available, because <c>PersistCompletion</c> copies it into
    /// the real install directory.
    /// </remarks>
    private string UninstallAnchorFallback(StepContext ctx)
    {
        var imageDir = Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(imageDir))
        {
            return imageDir;
        }
        return string.IsNullOrWhiteSpace(ctx.InstallDir)
            ? Path.Combine(ScopeLayout.For(_scope).InstallRoot, _blob.AppId)
            : ctx.InstallDir;
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
        // P7: the interactive uninstall (uninstall.exe /LOG, double-clicked) logs
        // the same reversal trail as the headless path.
        EnsureLog();
        var effectiveProgress = _log is null ? progress : new LoggingProgress(progress, _log);
        var ctx = BuildUninstallContext();

        // P2: pre_uninstall hooks (abort on failure) around the journal replay.
        var preHook = await HookRunner.RunAsync(
            "pre_uninstall", _blob.HookPreUninstall, ctx, effectiveProgress, ct).ConfigureAwait(false);
        if (!preHook.Success)
        {
            var msg = $"pre_uninstall hook '{preHook.FailedStepId}' failed: {preHook.Error}";
            _log?.WriteLine($"result: uninstall aborted — {ctx.Redact(msg)}");
            return new InstallOutcome(false, msg);
        }

        // R1 clause (c): anchored to the install dir resolved from the signed blob.
        var result = await new UninstallEngine()
            .RunAsync(_blob.AppId, UninstallAnchorFallback(ctx), _scope, effectiveProgress, ct)
            .ConfigureAwait(false);

        if (result.Success)
        {
            await HookRunner.RunAsync(
                "post_uninstall", _blob.HookPostUninstall, ctx, effectiveProgress, ct).ConfigureAwait(false);
        }

        _log?.WriteLine(result.Success ? "result: uninstall success" : $"result: uninstall failed — {result.Error}");
        return new InstallOutcome(result.Success, result.Error);
    }

    // --- P6 (gaps G7/G17): files-in-use ---

    /// <summary>
    /// Scan for anything blocking this run (P6): a declared <c>installer.app_mutex</c>
    /// that is held, or a process the Restart Manager reports holding a file open
    /// under <paramref name="installDir"/> (defaults to the destination this run would
    /// use). Empty means clear. Used by the wizard's "Close applications" screen and,
    /// defensively, by the install path itself.
    /// </summary>
    public IReadOnlyList<AppBlocker> ScanBlockers(string? installDir = null)
        => FilesInUse.Scan(_blob.AppMutex, installDir ?? EffectiveInstallDir());

    /// <summary>
    /// Ask the Restart Manager to gracefully close the applications holding the
    /// install directory (P6) — the wizard's "Close for me" and the silent
    /// <c>/closeapps</c> path. No restart is attempted and nothing is force-killed;
    /// the caller re-scans to confirm the blockers are gone.
    /// </summary>
    public bool CloseBlockers(string? installDir = null)
        => FilesInUse.CloseBlockers(installDir ?? EffectiveInstallDir());

    /// <summary>The destination this run resolves to — the wizard's pick when made, else the computed default.</summary>
    private string EffectiveInstallDir() => CollectedInstallDir ?? ResolveDefaultInstallDir();

    /// <summary>
    /// The files-in-use gate (P6, gap G7). Runs after the destination is known and
    /// BEFORE prerequisites, the prior-version teardown, and the rollback journal — so
    /// a blocked run changes nothing at all. With <c>/closeapps</c> the blockers are
    /// closed via the Restart Manager and re-scanned; without it the run is refused
    /// (headless maps this onto <see cref="FilesInUseExitCode"/>). A wizard run has
    /// already cleared blockers on the Close-applications screen, so this is normally
    /// a no-op there.
    /// </summary>
    private InstallOutcome? CheckFilesInUse(StepContext ctx, string? installDir, IProgress<StepProgress>? progress)
    {
        var blockers = FilesInUse.Scan(_blob.AppMutex, installDir);
        if (blockers.Count == 0)
        {
            return null; // clear
        }

        if (_parsed.CloseApps)
        {
            Report(progress, ctx, $"close-apps: closing {blockers.Count} blocking application(s)…", isError: false);
            FilesInUse.CloseBlockers(installDir);
            blockers = FilesInUse.Scan(_blob.AppMutex, installDir);
            if (blockers.Count == 0)
            {
                Report(progress, ctx, "close-apps: all blocking applications closed", isError: false);
                return null;
            }
        }

        _blockedByFilesInUse = true;
        var message = BuildBlockerMessage(blockers);
        foreach (var b in blockers)
        {
            Report(progress, ctx, $"blocked by: {b.Describe()}", isError: true);
        }
        _log?.WriteLine($"result: blocked — {message}");
        return new InstallOutcome(false, message);
    }

    // P9 design D2: NOT migrated. Lowercase-prefixed (not sentence-cased) — the
    // same tell that marks every log-convention line in this file as staying
    // English — and it names the CLI-only /closeapps flag, mirroring the
    // "blocked by: ..." / "close-apps: ..." progress lines around its call site
    // that are log convention for the same reason.
    private static string BuildBlockerMessage(IReadOnlyList<AppBlocker> blockers)
    {
        var names = new List<string>(blockers.Count);
        foreach (var b in blockers)
        {
            names.Add(b.Describe());
        }
        return
            "these applications are using files this install needs and must be closed first: " +
            string.Join(", ", names) +
            " — close them and retry, or re-run with /closeapps";
    }

    private static void Report(IProgress<StepProgress>? progress, StepContext ctx, string message, bool isError)
        => progress?.Report(new StepProgress(0, 0, ctx.Redact(message), isError));

    // --- P2 (gap G4): run-after-install launch ---

    /// <summary>True when the manifest declares an <c>installer.run_after_install</c> target.</summary>
    public bool HasRunAfterInstall => !string.IsNullOrEmpty(_blob.RunAfterInstallPath);

    /// <summary>The Done-screen checkbox label, e.g. "Launch Acme Studio".</summary>
    public string LaunchLabel =>
        Strings.FinishLaunchApp(SessionLanguage.Current,
            _blob.AppName ?? _blob.DisplayName ?? Strings.BrandAppFallback(SessionLanguage.Current));

    /// <summary>
    /// Start the <c>run_after_install</c> target UNELEVATED (P2, gap G4), resolving
    /// its <c>{install_dir}</c> / <c>{var.*}</c> tokens against the same context the
    /// install used. Best-effort: returns false and never throws when the target is
    /// absent, unresolvable, or fails to start.
    /// </summary>
    public bool LaunchAppUnelevated()
    {
        if (!HasRunAfterInstall)
        {
            return false;
        }
#pragma warning disable CA1031 // Launch is a convenience: never fault the install on a bad target.
        try
        {
            var ctx = StepContext.From(
                _blob, _parsed, payloadRoot: null, collected: _collectedValues,
                scope: _scope, collectedOptions: _collectedOptions, collectedInstallDir: CollectedInstallDir);

            var path = ctx.ResolvePath(_blob.RunAfterInstallPath!);
            System.Collections.Generic.List<string>? args = null;
            if (_blob.RunAfterInstallArgs is { Count: > 0 } raw)
            {
                args = new System.Collections.Generic.List<string>(raw.Count);
                foreach (var a in raw)
                {
                    args.Add(ctx.Resolve(a));
                }
            }

            _log?.WriteLine($"launch: {ctx.Redact(path)}");
            return Launcher.LaunchUnelevated(path, args);
        }
        catch (Exception)
        {
            _log?.WriteLine("launch: failed to resolve or start run_after_install target");
            return false;
        }
#pragma warning restore CA1031
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
