using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using SigilBuild.Installer.Host.Branding;
using SigilBuild.Wrapper.Engine;

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

    public InstallerViewModel(BrandTokens tokens)
    {
        Brand = tokens;
        _installPath = System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles),
            tokens.AppName);
    }

    public BrandTokens Brand { get; }

    public InstallerOutcomeCode OutcomeCode { get; private set; } = InstallerOutcomeCode.Completed;

    /// <summary>Growing log of the engine's copy/reg/path/link output for the Installing + Failed screens.</summary>
    public ObservableCollection<InstallLogLine> LogLines { get; } = new();

    public InstallerStep CurrentStep
    {
        get => _step;
        set { if (_step != value) { _step = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanGoBack)); OnPropertyChanged(nameof(CanGoNext)); OnPropertyChanged(nameof(CanCancel)); } }
    }

    public bool CanGoBack => _step is not InstallerStep.Welcome and not InstallerStep.Installing and not InstallerStep.Finish and not InstallerStep.Failed;
    public bool CanGoNext => _step is not InstallerStep.Installing and not InstallerStep.Finish and not InstallerStep.Failed;

    /// <summary>False only on the Finish screen — install is already done, nothing to cancel.</summary>
    public bool CanCancel => _step is not InstallerStep.Finish;

    public string LicenseText { get; set; } = "MIT License (placeholder — replace with the app's actual EULA).";

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
        var target = _step switch
        {
            InstallerStep.Welcome => InstallerStep.License,
            InstallerStep.License => LicenseAccepted ? InstallerStep.InstallOptions : _step,
            InstallerStep.InstallOptions => InstallerStep.Installing,
            _ => _step,
        };

        if (target == _step)
        {
            return;
        }

        var enteringInstalling = target == InstallerStep.Installing;
        CurrentStep = target;

        if (enteringInstalling)
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

    public void Back() => CurrentStep = _step switch
    {
        InstallerStep.License => InstallerStep.Welcome,
        InstallerStep.InstallOptions => InstallerStep.License,
        InstallerStep.Installing => _step,
        _ => _step,
    };

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
