using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using SigilBuild.Installer.Host.Branding;
using SigilBuild.Wrapper.Core.Localization;

namespace SigilBuild.Installer.Host.ViewModels;

/// <summary>
/// The headed, non-silent <c>/Update</c> flow (T12.4): a minimal branded window
/// that starts checking for an update as soon as it opens (no confirm gesture —
/// unlike <see cref="UninstallViewModel"/>'s confirm → progress → done), and
/// drives the real <see cref="InstallSession.RunUpdateInteractiveAsync"/>. States:
/// <c>Checking</c> → either <c>UpToDate</c> (nothing to do, exit 0) or
/// <c>Downloading</c> → <c>LaunchingChild</c> (the new version's own install
/// wizard takes over) → <c>Done</c>/<c>Failed</c> once that child exits.
/// </summary>
public enum UpdateStep { Checking, Downloading, LaunchingChild, UpToDate, Done, Failed }

public sealed class UpdateViewModel : INotifyPropertyChanged
{
    // T12.3's UpdateRunner reports stages as plain (message, isError) pairs — see
    // its remarks. These two substrings are the stable, internal-only markers this
    // ViewModel keys off to move the progress display forward; if UpdateRunner's
    // wording ever changes, update both call sites together.
    private const string NewerVersionAvailableMarker = "newer version available";
    private const string InstallingChildMarker = "running the downloaded setup";

    private Func<Action<string, bool>, CancellationToken, Task<int>>? _runner;
    private CancellationTokenSource? _cts;
    private UpdateStep _step = UpdateStep.Checking;

    // Set SYNCHRONOUSLY inside Report (whichever thread UpdateRunner happens to be
    // running on) — never behind the Progress<T> marshal below. The terminal-state
    // decision in RunAsync reads these right after `await _runner(...)` resumes, and
    // a Task's completion is itself a happens-before edge, so these are guaranteed
    // visible there with no extra synchronization needed. Kept separate from the
    // (purely cosmetic, UI-thread-marshaled) LogLines/CurrentStep updates below so
    // the decision never races a pending dispatcher post.
    private bool _sawNewerVersionAvailable;
    private bool _lastReportWasError;
    private string? _lastReportMessage;

    // P9: the resolved chrome language for this session, captured once at
    // construction (Task 4 sets SessionLanguage before any UI is built).
    private readonly Lang _lang = SessionLanguage.Current;

    public UpdateViewModel(BrandTokens tokens)
    {
        Brand = tokens;
    }

    public BrandTokens Brand { get; }

    /// <summary>Growing log of UpdateRunner's reported stages, for the progress screen.</summary>
    public ObservableCollection<InstallLogLine> LogLines { get; } = new();

    /// <summary>
    /// The process exit code surfaced to <c>App.OutcomeExitCode</c>: 0 (up to date,
    /// or the launched child succeeded), the launched child's own code (e.g. 3010
    /// reboot-required), or one of <c>InstallSession</c>'s <c>Update*ExitCode</c>
    /// constants. Null until <see cref="Start"/>'s run completes.
    /// </summary>
    public int? OutcomeExitCode { get; private set; }

    public UpdateStep CurrentStep
    {
        get => _step;
        private set
        {
            if (_step != value)
            {
                _step = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsChecking));
                OnPropertyChanged(nameof(IsDownloading));
                OnPropertyChanged(nameof(IsLaunchingChild));
                OnPropertyChanged(nameof(IsProgress));
                OnPropertyChanged(nameof(IsUpToDate));
                OnPropertyChanged(nameof(IsDone));
                OnPropertyChanged(nameof(IsFailed));
                OnPropertyChanged(nameof(IsFinished));
            }
        }
    }

    public bool IsChecking => _step == UpdateStep.Checking;
    public bool IsDownloading => _step == UpdateStep.Downloading;
    public bool IsLaunchingChild => _step == UpdateStep.LaunchingChild;

    /// <summary>True for any of the three non-terminal stages — the progress screen's visibility.</summary>
    public bool IsProgress => _step is UpdateStep.Checking or UpdateStep.Downloading or UpdateStep.LaunchingChild;

    public bool IsUpToDate => _step == UpdateStep.UpToDate;
    public bool IsDone => _step == UpdateStep.Done;
    public bool IsFailed => _step == UpdateStep.Failed;

    /// <summary>True on any terminal screen — the footer shows a single Close.</summary>
    public bool IsFinished => _step is UpdateStep.UpToDate or UpdateStep.Done or UpdateStep.Failed;

    /// <summary>The progress screen's status heading, reflecting the current stage.</summary>
    public string StatusHeading => _step switch
    {
        UpdateStep.Downloading => Strings.UpdateDownloading(_lang),
        UpdateStep.LaunchingChild => Strings.UpdateLaunching(_lang),
        _ => Strings.UpdateChecking(_lang),
    };

    public string UpToDateMessage => Strings.UpdateUpToDate(_lang, Brand.AppName);

    public string DoneMessage => Strings.UpdateDone(_lang, Brand.AppName);

    /// <summary>The rail's version line, mirroring <c>UninstallViewModel.VersionLine</c>.</summary>
    public string VersionLine => Strings.UpdateVersion(_lang, Brand.AppVersion);

    private string? _errorMessage;
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set { _errorMessage = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Wire the real update driver (an <see cref="InstallSession"/>-backed
    /// delegate). Left null in unit tests that drive the step machine directly.
    /// </summary>
    public void ConfigureRunner(Func<Action<string, bool>, CancellationToken, Task<int>> runner)
        => _runner = runner;

    /// <summary>
    /// The running (or completed) update operation. Null until <see cref="Start"/>
    /// is called. Exposed so the shell and tests can await completion.
    /// </summary>
    public Task? UpdateTask { get; private set; }

    /// <summary>
    /// Begin checking for an update. Unlike <see cref="UninstallViewModel.Confirm"/>,
    /// there is no user gesture to wait for — the headed window starts working as
    /// soon as it is shown. A no-op if already started.
    /// </summary>
    public void Start()
    {
        if (UpdateTask is not null)
        {
            return;
        }
        UpdateTask = RunAsync();
    }

    private async Task RunAsync()
    {
        if (_runner is null)
        {
            return; // not wired (unit tests): navigation is driven manually.
        }

        LogLines.Clear();
        ErrorMessage = null;
        _sawNewerVersionAvailable = false;
        _lastReportWasError = false;
        _lastReportMessage = null;

        var cts = new CancellationTokenSource();
        _cts = cts;

        // Cosmetic-only UI updates (the growing log + the intermediate
        // Downloading/LaunchingChild step labels), marshaled to this VM's captured
        // synchronization context exactly like InstallerViewModel.ApplyProgress /
        // UninstallViewModel.ApplyProgress — UpdateRunner may report from a
        // thread-pool continuation (its internal awaits use ConfigureAwait(false)),
        // and an ObservableCollection must only be mutated on the UI thread.
        // SerialProgress, not Progress<T>: with no SynchronizationContext the BCL type
        // posts every report to the thread pool, so sequential reports can race inside
        // ApplyReport's LogLines.Add. See SerialProgress.
        var uiUpdates =
            new SerialProgress<(string Message, bool IsError)>(item => ApplyReport(item.Message, item.IsError));

        void Report(string message, bool isError)
        {
            // Synchronous bookkeeping the terminal decision below depends on — see
            // the fields' remarks for why this must NOT go through uiUpdates.
            _lastReportMessage = message;
            _lastReportWasError = isError;
            if (message.Contains(NewerVersionAvailableMarker, StringComparison.OrdinalIgnoreCase))
            {
                _sawNewerVersionAvailable = true;
            }

            uiUpdates.Report((message, isError));
        }

        try
        {
            var code = await _runner(Report, cts.Token).ConfigureAwait(true);
            OutcomeExitCode = code;

            if (!_sawNewerVersionAvailable && code == 0)
            {
                // Nothing to download — the checked version is already current.
                CurrentStep = UpdateStep.UpToDate;
            }
            else if (_sawNewerVersionAvailable && !_lastReportWasError)
            {
                // A newer version was downloaded and the launched child (headed:
                // its own wizard) finished without the runner flagging its last
                // report as an error.
                CurrentStep = UpdateStep.Done;
            }
            else
            {
                ErrorMessage = _lastReportMessage;
                CurrentStep = UpdateStep.Failed;
            }
        }
        catch (OperationCanceledException)
        {
            OutcomeExitCode = 2;
            CurrentStep = UpdateStep.Failed;
        }
        finally
        {
            _cts = null;
            cts.Dispose();
        }
    }

    private void ApplyReport(string message, bool isError)
    {
        LogLines.Add(new InstallLogLine(message, isError));

        if (message.Contains(NewerVersionAvailableMarker, StringComparison.OrdinalIgnoreCase))
        {
            CurrentStep = UpdateStep.Downloading;
        }
        else if (message.Contains(InstallingChildMarker, StringComparison.OrdinalIgnoreCase))
        {
            CurrentStep = UpdateStep.LaunchingChild;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name ?? ""));
}
