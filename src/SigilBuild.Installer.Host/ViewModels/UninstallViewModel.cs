using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using SigilBuild.Installer.Host.Branding;
using SigilBuild.Wrapper.Engine;

namespace SigilBuild.Installer.Host.ViewModels;

/// <summary>
/// The three-state interactive uninstall flow (spec T15 / design brief): a branded
/// <c>confirm → progress → done</c> sequence (plus a <c>failed</c> terminal) that
/// drives the real <see cref="UninstallEngine"/> through
/// <see cref="InstallSession.RunUninstallInteractiveAsync"/>. Deliberately kept
/// separate from <see cref="InstallerViewModel"/> — the uninstall window is its own
/// minimal flow, so the two never share navigation/rail state.
/// </summary>
public enum UninstallStep { Confirm, Progress, Done, Failed }

public sealed class UninstallViewModel : INotifyPropertyChanged
{
    private Func<IProgress<StepProgress>, CancellationToken, Task<InstallOutcome>>? _runner;
    private CancellationTokenSource? _cts;
    private UninstallStep _step = UninstallStep.Confirm;

    public UninstallViewModel(BrandTokens tokens)
    {
        Brand = tokens;
    }

    public BrandTokens Brand { get; }

    /// <summary>Growing log of the engine's reversal output for the Progress / Failed screens.</summary>
    public ObservableCollection<InstallLogLine> LogLines { get; } = new();

    /// <summary>
    /// Process exit code surfaced to <c>Program.Main</c>: <c>0</c> removed,
    /// <c>1</c> uninstall failed, <c>2</c> user cancelled before it ran. Reuses the
    /// install flow's <see cref="InstallerOutcomeCode"/> so both share one contract.
    /// </summary>
    public InstallerOutcomeCode OutcomeCode { get; private set; } = InstallerOutcomeCode.Completed;

    public UninstallStep CurrentStep
    {
        get => _step;
        set
        {
            if (_step != value)
            {
                _step = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsConfirm));
                OnPropertyChanged(nameof(IsProgress));
                OnPropertyChanged(nameof(IsDone));
                OnPropertyChanged(nameof(IsFailed));
                OnPropertyChanged(nameof(IsFinished));
            }
        }
    }

    public bool IsConfirm => _step == UninstallStep.Confirm;
    public bool IsProgress => _step == UninstallStep.Progress;
    public bool IsDone => _step == UninstallStep.Done;
    public bool IsFailed => _step == UninstallStep.Failed;

    /// <summary>True on either terminal screen (Done / Failed) — the footer shows a single Close.</summary>
    public bool IsFinished => _step is UninstallStep.Done or UninstallStep.Failed;

    /// <summary>The confirm-screen body copy (design brief wording).</summary>
    public string ConfirmMessage =>
        $"This removes {Brand.AppName}, its Start-menu entry, desktop shortcut, and PATH entry. Your documents are not affected.";

    public string ConfirmTitle => $"Uninstall {Brand.AppName}";

    public string DoneMessage => $"{Brand.AppName} was removed";

    private double _progress;
    public double UninstallProgress
    {
        get => _progress;
        private set { _progress = value; OnPropertyChanged(); }
    }

    private string _currentItem = "";
    public string UninstallCurrentItem
    {
        get => _currentItem;
        private set { _currentItem = value; OnPropertyChanged(); }
    }

    private string? _errorMessage;
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set { _errorMessage = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Wire the real uninstall driver (an <see cref="InstallSession"/>-backed
    /// delegate). Left null in unit tests that drive the step machine directly.
    /// </summary>
    public void ConfigureRunner(Func<IProgress<StepProgress>, CancellationToken, Task<InstallOutcome>> runner)
        => _runner = runner;

    /// <summary>
    /// The running (or completed) uninstall operation started when the user confirms.
    /// Null until then. Exposed so the shell and tests can await completion.
    /// </summary>
    public Task? UninstallTask { get; private set; }

    /// <summary>
    /// The user confirmed removal on the Confirm screen: advance to Progress and
    /// kick off the engine. A no-op off the Confirm screen.
    /// </summary>
    public void Confirm()
    {
        if (_step != UninstallStep.Confirm)
        {
            return;
        }
        CurrentStep = UninstallStep.Progress;
        UninstallTask = StartUninstallAsync();
    }

    /// <summary>
    /// The user dismissed the Confirm screen without uninstalling. Records a
    /// user-cancelled outcome (exit 2); the caller closes the window.
    /// </summary>
    public void CancelConfirm()
    {
        if (_step == UninstallStep.Confirm)
        {
            OutcomeCode = InstallerOutcomeCode.UserCancelled;
        }
    }

    private async Task StartUninstallAsync()
    {
        if (_runner is null)
        {
            return; // not wired (unit tests): navigation is driven manually.
        }

        LogLines.Clear();
        UninstallProgress = 0;
        ErrorMessage = null;

        var cts = new CancellationTokenSource();
        _cts = cts;
        var progress = new Progress<StepProgress>(ApplyProgress);

        try
        {
            var outcome = await _runner(progress, cts.Token).ConfigureAwait(true);
            if (outcome.Success)
            {
                UninstallProgress = 1;
                OutcomeCode = InstallerOutcomeCode.Completed;
                CurrentStep = UninstallStep.Done;
            }
            else
            {
                ErrorMessage = outcome.Error;
                OutcomeCode = InstallerOutcomeCode.Failed;
                CurrentStep = UninstallStep.Failed;
            }
        }
        finally
        {
            _cts = null;
            cts.Dispose();
        }
    }

    private void ApplyProgress(StepProgress p)
    {
        UninstallProgress = p.Fraction;
        if (p.Message is not null)
        {
            UninstallCurrentItem = p.Message;
            LogLines.Add(new InstallLogLine(p.Message, p.IsError));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name ?? ""));
}
