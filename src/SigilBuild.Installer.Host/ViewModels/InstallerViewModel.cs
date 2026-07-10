using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using SigilBuild.Core.Manifest;
using SigilBuild.Installer.Host.Branding;
using SigilBuild.Installer.Host.Services;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Expressions;

namespace SigilBuild.Installer.Host.ViewModels;

public enum InstallerStep { Welcome, License, InstallOptions, Options, Installing, Finish, Failed, Custom }

/// <summary>
/// Process exit code surfaced by the installer, per the unified T2 command-line
/// contract shared with the console wrapper: <c>0</c> ok, <c>1</c> step failure
/// (rolled back), <c>2</c> user cancelled (rolled back).
/// </summary>
public enum InstallerOutcomeCode
{
    Completed     = 0,
    Failed        = 1,
    UserCancelled = 2,
}

/// <summary>A single line in the Installing / Failed screen log.</summary>
public sealed record InstallLogLine(string Text, bool IsError);

public sealed class InstallerViewModel : INotifyPropertyChanged
{
    private InstallerStep _step = InstallerStep.Welcome;
    private string _installPath;
    private CancellationTokenSource? _engineCts;
    private Func<IProgress<StepProgress>, CancellationToken, Task<InstallOutcome>>? _installRunner;

    // T9 flow: an ordered list of wizard positions. Custom screens (declared over
    // parameters) are inserted before Installing. The flow is screen-list driven
    // rather than hardcoded so T14 (license) and later tasks can extend it.
    private readonly List<FlowNode> _flow = new();
    private int _flowIndex;
    private IReadOnlyList<CustomScreenViewModel> _customScreens = Array.Empty<CustomScreenViewModel>();
    private IReadOnlyList<ParameterDefinition> _parameters = Array.Empty<ParameterDefinition>();

    // T14: the License screen (and its rail entry) appear IFF the blob carries
    // license text. Absent by default so an un-stamped/dev host and a manifest
    // with no `installer.license` skip the screen entirely.
    private bool _hasLicense;

    // T8: the built-in Options screen (and its rail entry) appear IFF the blob
    // carries at least one enabled option component. Absent by default so an
    // un-stamped/dev host and a manifest with no `installer.options` skip it.
    private bool _hasOptions;

    public InstallerViewModel(BrandTokens tokens)
    {
        Brand = tokens;
        // Fallback default (T13): the per-user scope root — %LocalAppData%\Programs\
        // <App> — matching the auto→user default (decision 9). The host overrides
        // this via ConfigureDestination with the session's scope-aware resolution
        // (honoring /D= + the manifest install_dir). Kept user-writable so the
        // Destination gate passes without elevation.
        _installPath = System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            tokens.AppName);
        RebuildFlow();
    }

    public BrandTokens Brand { get; }

    // http-options runtime wiring: a source-backed enum field fetches its dropdown
    // options when its custom screen becomes visible. The fetch is injectable so
    // unit tests supply canned options without real network I/O; production uses
    // the real HTTP loader.
    private OptionsFetcher _optionsFetcher = HttpOptionsLoader.LoadAsync;

    /// <summary>
    /// Override the option source used by source-backed dropdowns (default:
    /// <see cref="HttpOptionsLoader.LoadAsync"/>). Tests inject a canned fetcher so
    /// no unit test hits the network.
    /// </summary>
    public void ConfigureOptionsFetcher(OptionsFetcher fetcher)
        => _optionsFetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));

    /// <summary>
    /// Kick off the dynamic option fetch for every source-backed dropdown on a
    /// custom screen. Fire-and-forget so navigation (and the UI thread) never
    /// blocks on the network; each field surfaces its own loading/error state and
    /// LoadDynamicOptionsAsync never throws. The <c>${parameters.*}</c> URL
    /// placeholders are substituted from the values collected so far.
    /// </summary>
    private void TriggerDynamicOptionLoads(CustomScreenViewModel screen)
    {
        var collected = CollectedParameterValues;
        foreach (var field in screen.Fields)
        {
            if (field.HasDynamicOptions)
            {
                _ = field.LoadDynamicOptionsAsync(_optionsFetcher, collected);
            }
        }
    }

    // T10: a prior install of this app was detected (ARP/state). The wizard shows a
    // reinstall notice on the Welcome screen; the engine performs uninstall-then-
    // install so the reinstall stays idempotent (no duplicate PATH/shortcuts/ARP).
    private bool _existingInstallDetected;

    /// <summary>
    /// True when the session found a prior install of this app in the resolved scope
    /// (T10). Drives the Welcome-screen reinstall notice; wired by the host from
    /// <c>InstallSession.ExistingInstallDetected</c>. The v1 repair/reinstall flow is
    /// uninstall-then-install, performed by the engine — this flag only informs the user.
    /// </summary>
    public bool ExistingInstallDetected
    {
        get => _existingInstallDetected;
        private set
        {
            if (_existingInstallDetected != value)
            {
                _existingInstallDetected = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ReinstallNotice));
            }
        }
    }

    /// <summary>
    /// The Welcome-screen notice shown when <see cref="ExistingInstallDetected"/> is
    /// set — empty otherwise. Explains that continuing reinstalls the app (the current
    /// version is removed first), the v1 repair/reinstall behaviour (T10).
    /// </summary>
    public string ReinstallNotice => _existingInstallDetected
        ? $"{Brand.AppName} is already installed. Continuing will reinstall it — the current version is removed first."
        : string.Empty;

    /// <summary>
    /// Wire the reinstall notice (T10). Called by the host from the session's
    /// <c>ExistingInstallDetected</c>. Idempotent; safe to call before the flow renders.
    /// </summary>
    public void SetExistingInstall(bool detected) => ExistingInstallDetected = detected;

    /// <summary>The declared custom screen currently shown (T9), or null off a custom screen.</summary>
    private CustomScreenViewModel? _currentCustomScreen;
    public CustomScreenViewModel? CurrentCustomScreen
    {
        get => _currentCustomScreen;
        private set { if (!ReferenceEquals(_currentCustomScreen, value)) { _currentCustomScreen = value; OnPropertyChanged(); } }
    }

    /// <summary>The rail step indicator, generated from the resolved (When-visible) screen set.</summary>
    public ObservableCollection<RailStep> RailSteps { get; } = new();

    /// <summary>
    /// Load the manifest-declared custom screens + parameter schema (T9) and rebuild
    /// the flow + rail. Called by the host once it has read them from the blob.
    /// </summary>
    public void LoadScreens(
        IReadOnlyList<InstallerScreen> screens, IReadOnlyList<ParameterDefinition> parameters)
    {
        _parameters = parameters ?? Array.Empty<ParameterDefinition>();
        var byName = new Dictionary<string, ParameterDefinition>(StringComparer.Ordinal);
        foreach (var p in _parameters)
        {
            byName[p.Name] = p;
        }

        var built = new List<CustomScreenViewModel>();
        foreach (var screen in screens ?? Array.Empty<InstallerScreen>())
        {
            var fields = new List<FieldViewModel>();
            foreach (var f in screen.Fields)
            {
                if (byName.TryGetValue(f.Param, out var def))
                {
                    fields.Add(new FieldViewModel(def, f.Widget));
                }
            }
            built.Add(new CustomScreenViewModel(
                screen.Id, Interpolate(screen.Title) ?? screen.Title, Interpolate(screen.Subtitle), screen.When, fields));
        }

        _customScreens = built;
        RebuildFlow();
    }

    /// <summary>
    /// Load the embedded license text (T14). Called by the host once it has read
    /// it from the blob (<c>InstallerLicenseLoader.LoadFromSelf()</c>). When the
    /// text is present the License screen + its rail entry appear (after the
    /// destination screen, per decision 4); when null/blank they are absent.
    /// Only the interactive wizard consults this — the headless <c>/silent</c>
    /// path never shows the License screen, so silent installs imply acceptance.
    /// </summary>
    public void LoadLicense(string? licenseText)
    {
        _hasLicense = !string.IsNullOrWhiteSpace(licenseText);
        if (_hasLicense)
        {
            LicenseText = licenseText!;
        }
        RebuildFlow();
    }

    /// <summary>
    /// The built-in option components rendered on the Options screen (T8), one
    /// checkbox each. Populated by <see cref="LoadOptions"/> from the blob; empty
    /// when the manifest declared no enabled option and the screen is absent.
    /// </summary>
    public ObservableCollection<OptionItemViewModel> OptionItems { get; } = new();

    /// <summary>
    /// Load the enabled built-in option components (T8). Called by the host once it
    /// has read them from the blob (via <c>InstallSession.Options</c>). When ≥ 1
    /// component is present the Options screen + its rail entry appear (after the
    /// License screen, per decision 4); when none, they are omitted. Each checkbox
    /// is seeded from the component's resolved default; <c>locked</c> components
    /// render disabled (always applied). Only the interactive wizard consults this —
    /// the headless <c>/silent</c> path resolves options to their manifest defaults.
    /// </summary>
    public void LoadOptions(IReadOnlyList<InstallerOptionComponent> options)
    {
        OptionItems.Clear();
        foreach (var component in options ?? Array.Empty<InstallerOptionComponent>())
        {
            OptionItems.Add(new OptionItemViewModel(component));
        }
        _hasOptions = OptionItems.Count > 0;
        RebuildFlow();
    }

    /// <summary>
    /// The wizard-collected option checkbox states, keyed by canonical component
    /// name — the exact map the engine binds into <c>option.*</c> for step gating.
    /// </summary>
    public IReadOnlyDictionary<string, bool> CollectedOptionValues
    {
        get
        {
            var dict = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var item in OptionItems)
            {
                dict[item.Name] = item.IsChecked;
            }
            return dict;
        }
    }

    /// <summary>
    /// The wizard-collected parameter values, keyed by canonical parameter name, as
    /// strings — the exact map the engine binds into <c>param.*</c> / <c>parameters.*</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string> CollectedParameterValues
    {
        get
        {
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var screen in _customScreens)
            {
                foreach (var f in screen.Fields)
                {
                    dict[f.ParamName] = f.GetStringValue();
                }
            }
            return dict;
        }
    }

    public InstallerOutcomeCode OutcomeCode { get; private set; } = InstallerOutcomeCode.Completed;

    /// <summary>Growing log of the engine's copy/reg/path/link output for the Installing + Failed screens.</summary>
    public ObservableCollection<InstallLogLine> LogLines { get; } = new();

    public InstallerStep CurrentStep
    {
        get => _step;
        set
        {
            if (_step != value)
            {
                _step = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanGoBack));
                OnPropertyChanged(nameof(CanGoNext));
                OnPropertyChanged(nameof(CanCancel));
                // When the step is set directly (tests, engine outcome) rather than
                // through Next/Back, resync the flow cursor so navigation stays correct.
                if (!_navigating)
                {
                    SyncFlowIndexToStep();
                }
            }
        }
    }

    private bool _navigating;

    private void SyncFlowIndexToStep()
    {
        for (var i = 0; i < _flow.Count; i++)
        {
            if (_flow[i].Step == _step)
            {
                _flowIndex = i;
                CurrentCustomScreen = _flow[i].Screen;
                RebuildRail();
                return;
            }
        }
        // Terminal step (Finish/Failed) not in the linear flow: leave the cursor.
    }

    public bool CanGoBack => _step is not InstallerStep.Welcome and not InstallerStep.Installing and not InstallerStep.Finish and not InstallerStep.Failed;
    public bool CanGoNext => _step is not InstallerStep.Installing and not InstallerStep.Finish and not InstallerStep.Failed;

    /// <summary>False only on the Finish screen — install is already done, nothing to cancel.</summary>
    public bool CanCancel => _step is not InstallerStep.Finish;

    /// <summary>
    /// The embedded license text shown on the License screen (T14). Empty until
    /// <see cref="LoadLicense"/> supplies the blob's text; an empty value means no
    /// license is embedded and the License screen is absent from the flow.
    /// </summary>
    public string LicenseText { get; set; } = string.Empty;

    private bool _licenseAccepted;
    public bool LicenseAccepted
    {
        get => _licenseAccepted;
        set { if (_licenseAccepted != value) { _licenseAccepted = value; OnPropertyChanged(); } }
    }

    private bool _launchAfterInstall = true;
    public bool LaunchAfterInstall
    {
        get => _launchAfterInstall;
        set { if (_launchAfterInstall != value) { _launchAfterInstall = value; OnPropertyChanged(); } }
    }

    public string InstallPath
    {
        get => _installPath;
        set
        {
            if (_installPath != value)
            {
                _installPath = value;
                OnPropertyChanged();
                // Re-validate live so a corrected path clears the inline error and
                // re-enables Next without needing a second click.
                if (_step == InstallerStep.InstallOptions)
                {
                    ValidateDestination();
                }
            }
        }
    }

    // --- Destination screen (T13): scope toggle + inline path validation ---

    private Func<bool, string>? _defaultPathResolver;
    private bool _scopeSelectable;

    /// <summary>
    /// Whether the Destination screen shows the user/machine scope radios (T12
    /// <c>scope: auto</c>). A manifest that fixes the scope hides them.
    /// </summary>
    public bool ScopeSelectable
    {
        get => _scopeSelectable;
        private set { if (_scopeSelectable != value) { _scopeSelectable = value; OnPropertyChanged(); } }
    }

    private bool _isMachineScope;

    /// <summary>
    /// The "All users of this computer" scope radio. Selecting it swaps the
    /// pre-filled install path to the machine scope root (Program Files), per the
    /// design brief. Bound two-way; paired with <see cref="IsUserScope"/>.
    /// </summary>
    public bool IsMachineScope
    {
        get => _isMachineScope;
        set
        {
            if (_isMachineScope != value)
            {
                _isMachineScope = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsUserScope));
                // Toggling scope recomputes a clean default path for the new scope.
                if (_defaultPathResolver is not null)
                {
                    InstallPath = _defaultPathResolver(_isMachineScope);
                }
            }
        }
    }

    /// <summary>The "Just for me" scope radio — the inverse of <see cref="IsMachineScope"/>.</summary>
    public bool IsUserScope
    {
        get => !_isMachineScope;
        set { if (value) { IsMachineScope = false; } }
    }

    private string? _installPathError;

    /// <summary>
    /// The inline validation error shown under the path input on the Destination
    /// screen, or null when the path is valid. A non-null value blocks Next.
    /// </summary>
    public string? InstallPathError
    {
        get => _installPathError;
        private set
        {
            if (_installPathError != value)
            {
                _installPathError = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasInstallPathError));
            }
        }
    }

    /// <summary>True when <see cref="InstallPathError"/> is set (drives the error text visibility).</summary>
    public bool HasInstallPathError => !string.IsNullOrEmpty(_installPathError);

    /// <summary>
    /// Wire the Destination screen (T13): whether the scope radios show, a resolver
    /// that maps a scope selection (<c>isMachine</c>) to its default install path,
    /// and the initial pre-filled path. Called by the host from the session; unit
    /// tests may call it directly or drive <see cref="InstallPath"/> alone.
    /// </summary>
    public void ConfigureDestination(bool scopeSelectable, Func<bool, string> defaultPathResolver, string initialPath)
    {
        System.ArgumentNullException.ThrowIfNull(defaultPathResolver);
        _defaultPathResolver = defaultPathResolver;
        ScopeSelectable = scopeSelectable;
        InstallPath = initialPath;
        InstallPathError = null;
    }

    /// <summary>
    /// Validate the chosen install location before advancing (T13): non-blank,
    /// absolute, not an existing file, and writable — or elevatable when a machine
    /// scope is selected. Sets <see cref="InstallPathError"/> (inline, blocks Next)
    /// and returns false on failure; clears it and returns true on success.
    /// </summary>
    public bool ValidateDestination()
    {
        var path = _installPath?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            InstallPathError = "Enter an install location.";
            return false;
        }
        if (!System.IO.Path.IsPathFullyQualified(path))
        {
            InstallPathError = "Enter an absolute path (for example C:\\Program Files\\App).";
            return false;
        }
        if (System.IO.File.Exists(path))
        {
            InstallPathError = "That location is a file. Choose a folder.";
            return false;
        }
        if (!IsWritableOrElevatable(path))
        {
            InstallPathError = "You don't have permission to install there. Choose another folder.";
            return false;
        }

        InstallPathError = null;
        return true;
    }

    /// <summary>
    /// True when the target — or its nearest existing ancestor — is writable, or a
    /// machine-scope install is selected (elevation will grant the write). A target
    /// whose drive/parent chain does not exist at all is rejected.
    /// </summary>
    private bool IsWritableOrElevatable(string path)
    {
        var nearest = NearestExistingAncestor(path);
        if (nearest is null)
        {
            return false; // the drive / parent folder does not exist.
        }
        // A machine install elevates, so Program Files being unwritable unelevated
        // is fine — the elevated child performs the write.
        if (_isMachineScope)
        {
            return true;
        }
        return CanWriteInto(nearest);
    }

    private static string? NearestExistingAncestor(string path)
    {
        var current = path;
        while (!string.IsNullOrEmpty(current))
        {
            if (System.IO.Directory.Exists(current))
            {
                return current;
            }
            var parent = System.IO.Path.GetDirectoryName(current);
            if (string.Equals(parent, current, StringComparison.Ordinal))
            {
                break;
            }
            current = parent;
        }
        return null;
    }

    private static bool CanWriteInto(string directory)
    {
        var probe = System.IO.Path.Combine(directory, ".sigil-write-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (System.IO.File.Create(probe)) { }
            System.IO.File.Delete(probe);
            return true;
        }
        // Only a genuine permission denial blocks the install. Any other transient
        // IO quirk is given the benefit of the doubt (the engine rolls back if the
        // write later fails) so validation never flakes on an otherwise-valid path.
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (System.Security.SecurityException)
        {
            return false;
        }
#pragma warning disable CA1031 // Unexpected IO conditions must not falsely block a valid path.
        catch (Exception)
        {
            return true;
        }
#pragma warning restore CA1031
    }

    private double _installProgress;
    public double InstallProgress
    {
        get => _installProgress;
        set { _installProgress = value; OnPropertyChanged(); }
    }

    private string _installCurrentItem = "";
    public string InstallCurrentItem
    {
        get => _installCurrentItem;
        set { _installCurrentItem = value; OnPropertyChanged(); }
    }

    private string? _errorMessage;
    /// <summary>The engine's failure message, surfaced on the Failed screen.</summary>
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set { _errorMessage = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Wire the real install driver (an <see cref="InstallSession"/>-backed
    /// delegate). When set, entering the Installing screen kicks off the engine.
    /// Left null in unit tests, which drive the step machine directly.
    /// </summary>
    public void ConfigureInstallRunner(Func<IProgress<StepProgress>, CancellationToken, Task<InstallOutcome>> runner)
        => _installRunner = runner;

    public void Next()
    {
        // Destination gate (T13): a blank / relative / file / unwritable path shows
        // an inline error and blocks advancing off the destination screen.
        if (_flow[_flowIndex].Step == InstallerStep.InstallOptions && !ValidateDestination())
        {
            return;
        }

        // License gate: stay put until "I accept" is checked.
        if (_flow[_flowIndex].Step == InstallerStep.License && !LicenseAccepted)
        {
            return;
        }

        // Validate the current custom screen's fields before advancing (inline errors).
        if (_flow[_flowIndex].Screen is { } current && !current.Validate())
        {
            return;
        }

        var next = _flowIndex + 1;
        while (next < _flow.Count && _flow[next].Screen is { } s && !EvaluateWhen(s))
        {
            next++; // skip a declared screen whose `when` is false at runtime.
        }
        if (next >= _flow.Count)
        {
            return;
        }

        MoveTo(next);

        if (_flow[_flowIndex].Step == InstallerStep.Installing)
        {
            InstallTask = StartInstallAsync();
        }
    }

    /// <summary>
    /// The running (or completed) install operation started when the wizard
    /// entered the Installing screen. Null until then. Exposed so the shell and
    /// tests can observe completion; the UI itself reacts via
    /// <see cref="CurrentStep"/> transitions.
    /// </summary>
    public Task? InstallTask { get; private set; }

    public void Back()
    {
        if (_flowIndex == 0 || _flow[_flowIndex].Step == InstallerStep.Installing)
        {
            return;
        }

        var prev = _flowIndex - 1;
        while (prev > 0 && _flow[prev].Screen is { } s && !EvaluateWhen(s))
        {
            prev--; // skip hidden declared screens on the way back too.
        }
        MoveTo(prev);
    }

    // --- Flow machinery (T9): screen-list-driven navigation + rail generation ---

    private sealed record FlowNode(InstallerStep Step, CustomScreenViewModel? Screen);

    /// <summary>
    /// Rebuild the linear flow, per locked-design decision 4:
    /// welcome → destination → license? → [declared screens] → installing.
    /// The <see cref="InstallerStep.InstallOptions"/> node is the destination /
    /// "Install location" screen; the License screen (T14) is inserted after it,
    /// and only when <see cref="_hasLicense"/> is set. Declared screens follow.
    /// When-gating of declared screens is applied at navigation time, not here, so
    /// a screen can become visible after an earlier field is set.
    /// </summary>
    private void RebuildFlow()
    {
        _flow.Clear();
        _flow.Add(new FlowNode(InstallerStep.Welcome, null));
        _flow.Add(new FlowNode(InstallerStep.InstallOptions, null));
        if (_hasLicense)
        {
            _flow.Add(new FlowNode(InstallerStep.License, null));
        }
        // T8: the built-in Options screen sits after license, before the declared
        // screens (decision 4: welcome → destination → license? → options? → …).
        if (_hasOptions)
        {
            _flow.Add(new FlowNode(InstallerStep.Options, null));
        }
        foreach (var screen in _customScreens)
        {
            _flow.Add(new FlowNode(InstallerStep.Custom, screen));
        }
        _flow.Add(new FlowNode(InstallerStep.Installing, null));

        if (_flowIndex >= _flow.Count)
        {
            _flowIndex = 0;
        }
        RebuildRail();
    }

    private void MoveTo(int index)
    {
        _navigating = true;
        try
        {
            _flowIndex = index;
            var node = _flow[index];
            CurrentCustomScreen = node.Screen;
            CurrentStep = node.Step; // triggers the view swap

            // Entering a custom screen: fetch any source-backed dropdown's options
            // now that the earlier screens' values are available for URL substitution.
            if (node.Screen is { } screen)
            {
                TriggerDynamicOptionLoads(screen);
            }
        }
        finally
        {
            _navigating = false;
        }
        RebuildRail();
    }

    private void RebuildRail()
    {
        RailSteps.Clear();
        for (var i = 0; i < _flow.Count; i++)
        {
            var node = _flow[i];
            if (node.Screen is { } screen && !EvaluateWhen(screen))
            {
                continue; // hidden declared screen: absent from the rail.
            }

            var label = node.Step switch
            {
                InstallerStep.Welcome => "Welcome",
                InstallerStep.License => "License",
                InstallerStep.InstallOptions => "Location",
                InstallerStep.Options => "Options",
                InstallerStep.Installing => "Install",
                InstallerStep.Custom => node.Screen?.Id ?? "Configure",
                _ => node.Step.ToString(),
            };
            RailSteps.Add(new RailStep(label, isCurrent: i == _flowIndex, isDone: i < _flowIndex));
        }
    }

    /// <summary>
    /// Evaluate a declared screen's <c>when</c> against the current field values.
    /// No expression → always visible. An evaluation error fails open (screen
    /// shown) so a malformed manifest never hides a screen silently.
    /// </summary>
    private bool EvaluateWhen(CustomScreenViewModel screen)
    {
        if (string.IsNullOrWhiteSpace(screen.When))
        {
            return true;
        }

        var ctx = BuildExpressionContext();
        try
        {
            return new Evaluator().EvaluateBool(screen.When!, ctx);
        }
#pragma warning disable CA1031 // Fail-open: a bad `when` must not hide the screen or crash the wizard.
        catch (Exception)
        {
            return true;
        }
#pragma warning restore CA1031
    }

    private Dictionary<string, object?> BuildExpressionContext()
    {
        var collected = CollectedParameterValues;
        var ctx = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var def in _parameters)
        {
            object? value;
            if (collected.TryGetValue(def.Name, out var raw))
            {
                value = ConvertToTyped(raw, def.Type);
            }
            else
            {
                value = def.Default;
            }
            ctx["param." + def.Name] = value;
            ctx["parameters." + def.Name] = value;
        }
        return ctx;
    }

    private static object? ConvertToTyped(string? raw, ParameterType type)
    {
        if (raw is null)
        {
            return null;
        }
        return type switch
        {
            ParameterType.Bool => bool.TryParse(raw, out var b) ? b : (object)raw,
            ParameterType.Int => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
                ? i
                : (object)raw,
            _ => raw,
        };
    }

    private string? Interpolate(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }
        return text
            .Replace("{app.name}", Brand.AppName, StringComparison.Ordinal)
            .Replace("{app.version}", Brand.AppVersion, StringComparison.Ordinal)
            .Replace("{app.publisher}", Brand.Publisher, StringComparison.Ordinal);
    }

    /// <summary>
    /// Drive the real engine for the Installing screen: create the cancellation
    /// source, feed a <see cref="StepProgress"/> adapter into
    /// <see cref="InstallProgress"/> / <see cref="InstallCurrentItem"/> /
    /// <see cref="LogLines"/>, then route to Finish (success) or Failed (step
    /// failure). Cancellation is handled by <see cref="CancelAsync"/>; the engine
    /// rolls back and throws, which lands here as a no-op close.
    /// </summary>
    private async Task StartInstallAsync()
    {
        if (_installRunner is null)
        {
            return; // not wired (unit tests): navigation is driven manually.
        }

        LogLines.Clear();
        InstallProgress = 0;
        ErrorMessage = null;

        var cts = new CancellationTokenSource();
        SetEngineCts(cts);
        var progress = new Progress<StepProgress>(ApplyProgress);

        try
        {
            var outcome = await _installRunner(progress, cts.Token).ConfigureAwait(true);
            if (outcome.Success)
            {
                InstallProgress = 1;
                OutcomeCode = InstallerOutcomeCode.Completed;
                CurrentStep = InstallerStep.Finish;
            }
            else
            {
                ErrorMessage = outcome.Error;
                OutcomeCode = InstallerOutcomeCode.Failed;
                CurrentStep = InstallerStep.Failed;
            }
        }
        catch (OperationCanceledException)
        {
            // User cancelled: the engine already rolled back and CancelAsync set
            // the outcome + is closing the window. Nothing more to do here.
            OutcomeCode = InstallerOutcomeCode.UserCancelled;
        }
        finally
        {
            _engineCts = null;
            cts.Dispose();
        }
    }

    private void ApplyProgress(StepProgress p)
    {
        InstallProgress = p.Fraction;
        if (p.Message is not null)
        {
            InstallCurrentItem = p.Message;
            LogLines.Add(new InstallLogLine(p.Message, p.IsError));
        }
    }

    /// <summary>
    /// Registers the <see cref="CancellationTokenSource"/> owned by the install operation so
    /// <see cref="CancelAsync"/> can signal it when the user cancels during installation.
    /// </summary>
    public void SetEngineCts(CancellationTokenSource cts) => _engineCts = cts;

    /// <summary>
    /// Attempts to cancel the installation.  Returns <c>true</c> when the caller should
    /// close the window; <c>false</c> when the user dismissed the confirmation dialog.
    /// On the Finish screen this is always a no-op (returns <c>false</c>). On the Failed
    /// screen it closes the window preserving the failure exit code (never downgrades to
    /// "user cancelled").
    /// </summary>
    /// <param name="confirmAsync">
    /// A delegate that, when the install is actively running, must show a confirmation dialog
    /// and return <c>true</c> if the user confirms cancellation.  Pass <c>null</c> to skip the
    /// modal (used in automated tests for pre-install screens).
    /// </param>
    public async Task<bool> CancelAsync(Func<Task<bool>>? confirmAsync = null)
    {
        if (_step == InstallerStep.Finish)
            return false;   // install completed — no cancel

        if (_step == InstallerStep.Failed)
            return true;    // already failed + rolled back — close, keep exit code 1

        if (_step == InstallerStep.Installing && _engineCts is not null)
        {
            // Confirm with the user before interrupting a running install.
            if (confirmAsync is not null)
            {
                var confirmed = await confirmAsync().ConfigureAwait(true);
                if (!confirmed)
                    return false;
            }

            _engineCts.Cancel();
        }

        OutcomeCode = InstallerOutcomeCode.UserCancelled;
        return true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name ?? ""));
}
