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
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Expressions;

namespace SigilBuild.Installer.Host.ViewModels;

public enum InstallerStep { Welcome, License, InstallOptions, Installing, Finish, Failed, Custom }

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

    public InstallerViewModel(BrandTokens tokens)
    {
        Brand = tokens;
        _installPath = System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles),
            tokens.AppName);
        RebuildFlow();
    }

    public BrandTokens Brand { get; }

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
        set { if (_installPath != value) { _installPath = value; OnPropertyChanged(); } }
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
