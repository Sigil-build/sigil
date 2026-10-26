// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using SigilBuild.Installer.Host.Branding;
using SigilBuild.Wrapper.Core.Localization;
using SigilBuild.Wrapper.Engine;

namespace SigilBuild.Installer.Host.ViewModels;

/// <summary>
/// The steps of the interactive uninstall flow (design brief): a branded
/// <c>confirm → [close apps] → progress → done</c> sequence (plus a <c>failed</c>
/// terminal) that drives the real <see cref="UninstallEngine"/> through
/// <see cref="InstallSession.RunUninstallInteractiveAsync"/>. Deliberately kept
/// separate from <see cref="InstallerViewModel"/> — the uninstall window is its own
/// minimal flow, so the two never share navigation/rail state. They share only the
/// Close-applications screen, through <see cref="ICloseAppsGate"/>. (R103)
/// <c>NotInstalled</c> is the neutral terminal for an app with nothing left to remove —
/// a leftover <c>uninstall.exe</c> — and exits <c>0</c>. (R107)
/// </summary>
public enum UninstallStep { Confirm, CloseApps, Progress, Done, Failed, NotInstalled }

public sealed class UninstallViewModel : ICloseAppsGate
{
    private Func<IProgress<StepProgress>, CancellationToken, Task<InstallOutcome>>? _runner;
    private CancellationTokenSource? _cts;
    private UninstallStep _step = UninstallStep.Confirm;

    // The resolved chrome language for this session, captured once at
    // construction (SessionLanguage is set before any UI is built).
    private readonly Lang _lang = SessionLanguage.Current;

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
                OnPropertyChanged(nameof(IsCloseApps));
                OnPropertyChanged(nameof(IsProgress));
                OnPropertyChanged(nameof(IsDone));
                OnPropertyChanged(nameof(IsFailed));
                OnPropertyChanged(nameof(IsNotInstalled));
                OnPropertyChanged(nameof(IsFinished));
                OnPropertyChanged(nameof(IsCancellable));
            }
        }
    }

    public bool IsConfirm => _step == UninstallStep.Confirm;
    public bool IsCloseApps => _step == UninstallStep.CloseApps;
    public bool IsProgress => _step == UninstallStep.Progress;
    public bool IsDone => _step == UninstallStep.Done;
    public bool IsFailed => _step == UninstallStep.Failed;
    public bool IsNotInstalled => _step == UninstallStep.NotInstalled;

    /// <summary>
    /// True on every terminal screen (Done / Failed / NotInstalled) — the footer shows a
    /// single Close.
    /// </summary>
    public bool IsFinished => _step is UninstallStep.Done or UninstallStep.Failed or UninstallStep.NotInstalled;

    /// <summary>
    /// True on the steps where leaving removes nothing — Confirm and Close
    /// applications. The footer shows Cancel there, and closing the window records a
    /// user-cancelled outcome. (R103)
    /// </summary>
    public bool IsCancellable => _step is UninstallStep.Confirm or UninstallStep.CloseApps;

    /// <summary>The confirm-screen body copy (design brief wording).</summary>
    public string ConfirmMessage => Strings.UninstallBody(_lang, Brand.AppName);

    public string ConfirmTitle => Strings.UninstallTitle(_lang, Brand.AppName);

    public string DoneMessage => Strings.UninstallDone(_lang, Brand.AppName);

    /// <summary>The not-installed screen's heading, e.g. "Acme Studio is not installed". (R107)</summary>
    public string NotInstalledMessage => Strings.UninstallNotInstalled(_lang, Brand.AppName);

    /// <summary>
    /// There is nothing to uninstall: show the neutral not-installed screen instead of the
    /// confirmation, and leave with exit code <c>0</c>. A no-op once the uninstall has
    /// started. (R107)
    /// </summary>
    public void ShowNotInstalled()
    {
        if (_step is not (UninstallStep.Confirm or UninstallStep.CloseApps or UninstallStep.Progress))
        {
            return;
        }
        OutcomeCode = InstallerOutcomeCode.Completed;
        CurrentStep = UninstallStep.NotInstalled;
    }

    /// <summary>The rail's version line, bound by UninstallWindow.axaml.</summary>
    public string VersionLine => Strings.UninstallVersion(_lang, Brand.AppVersion);

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
    /// The runner is invoked on a thread-pool thread and must not touch this
    /// view-model or any UI object. (R95)
    /// </summary>
    /// <remarks>
    /// The runner may be invoked more than once: a blocked outcome
    /// (<see cref="InstallOutcome.IsBlocked"/>) moves the flow to the
    /// Close-applications step, and clearing it invokes the runner again to resume.
    /// The session runs its <c>pre_uninstall</c> hooks once and starts the replay only
    /// past a clear gate, so a second call is safe. (R103)
    /// </remarks>
    public void ConfigureRunner(Func<IProgress<StepProgress>, CancellationToken, Task<InstallOutcome>> runner)
        => _runner = runner;

    /// <summary>
    /// The running (or completed) uninstall operation started when the user confirms,
    /// or resumed from the Close-applications step. Null until then. Exposed so the
    /// shell and tests can await completion.
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
        LogLines.Clear();
        UninstallProgress = 0;
        StartOrResume();
    }

    /// <summary>
    /// The user left without uninstalling — Cancel on the Confirm or
    /// Close-applications step, or the window's close gesture there. Records a
    /// user-cancelled outcome (exit 2); nothing has been removed. The caller closes
    /// the window. A no-op on any other step. (R103)
    /// </summary>
    public void Cancel()
    {
        if (IsCancellable)
        {
            OutcomeCode = InstallerOutcomeCode.UserCancelled;
        }
    }

    // --- Close-running-applications gate (R103) ---

    private Func<IReadOnlyList<string>>? _blockerScan;
    private Action? _blockerClose;

    /// <inheritdoc />
    public ObservableCollection<string> Blockers { get; } = new();

    /// <summary>True when the last scan found something blocking.</summary>
    public bool HasBlockers => Blockers.Count > 0;

    /// <summary>
    /// Wire the files-in-use probes for the Close-applications step:
    /// <paramref name="scan"/> returns the current blocker descriptions over the
    /// directory the uninstall's own gate sweeps; <paramref name="close"/> asks the
    /// Restart Manager to close them. Left unwired, Retry and Close-for-me resume the
    /// run directly and the session's gate decides. (R103)
    /// </summary>
    public void ConfigureBlockerProbe(Func<IReadOnlyList<string>> scan, Action close)
    {
        _blockerScan = scan ?? throw new ArgumentNullException(nameof(scan));
        _blockerClose = close ?? throw new ArgumentNullException(nameof(close));
    }

    private bool _isClosingApps;

    /// <summary>
    /// True while "Close for me" is waiting on the Restart Manager. The screen shows
    /// a closing notice and disables Retry and Close-for-me until it returns. (R103)
    /// </summary>
    public bool IsClosingApps
    {
        get => _isClosingApps;
        private set
        {
            if (_isClosingApps != value)
            {
                _isClosingApps = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanActOnBlockers));
            }
        }
    }

    /// <inheritdoc />
    public bool CanActOnBlockers => !_isClosingApps;

    private bool _closeAppsFailed;

    /// <inheritdoc />
    public string CloseAppsBody => Strings.CloseAppsUninstallBody(_lang);

    /// <summary>
    /// True when the last "Close for me" left applications running; the screen then
    /// says Setup could not close them. Cleared by the next attempt or Retry. (R103)
    /// </summary>
    public bool CloseAppsFailed
    {
        get => _closeAppsFailed;
        private set
        {
            if (_closeAppsFailed != value)
            {
                _closeAppsFailed = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// "Retry" — the user closed the apps themselves. Re-scans and resumes the
    /// uninstall when clear; otherwise stays with the refreshed list. (R103)
    /// </summary>
    public void RetryBlockers()
    {
        if (_step != UninstallStep.CloseApps || _isClosingApps)
        {
            return;
        }
        CloseAppsFailed = false;
        if (_blockerScan is not null)
        {
            PublishBlockers(_blockerScan());
            if (Blockers.Count > 0)
            {
                return;
            }
        }
        StartOrResume();
    }

    /// <summary>
    /// "Close for me" — ask the Restart Manager to close the blocking applications
    /// off the UI thread, then re-scan. Resumes the uninstall when that cleared them;
    /// otherwise stays with the fresh list and says Setup could not close them. (R103)
    /// </summary>
    public async Task CloseBlockingAppsAsync()
    {
        if (_step != UninstallStep.CloseApps || _isClosingApps)
        {
            return;
        }

        var close = _blockerClose;
        var scan = _blockerScan;
        CloseAppsFailed = false;
        IsClosingApps = true;
        IReadOnlyList<string> remaining;
        try
        {
            remaining = await CloseAppsWork.CloseThenRescanAsync(close, scan).ConfigureAwait(true);
        }
        finally
        {
            IsClosingApps = false;
        }

        PublishBlockers(remaining);
        if (Blockers.Count == 0)
        {
            StartOrResume();
            return;
        }
        CloseAppsFailed = true;
    }

    private void PublishBlockers(IReadOnlyList<string> found)
    {
        Blockers.Clear();
        foreach (var b in found)
        {
            Blockers.Add(b);
        }
        OnPropertyChanged(nameof(HasBlockers));
    }

    private void StartOrResume()
    {
        CurrentStep = UninstallStep.Progress;
        UninstallTask = StartUninstallAsync();
    }

    private async Task StartUninstallAsync()
    {
        if (_runner is null)
        {
            return; // not wired (unit tests): navigation is driven manually.
        }

        ErrorMessage = null;

        var cts = new CancellationTokenSource();
        _cts = cts;
        // SerialProgress, not Progress<T>: with no SynchronizationContext the BCL type
        // posts every report to the thread pool, so reports the engine made sequentially
        // can execute ApplyProgress concurrently and race LogLines. See SerialProgress.
        // Constructed on the UI thread, so it captures the UI context and the
        // background runner's reports are posted back to it.
        var progress = new SerialProgress<StepProgress>(ApplyProgress);
        var runner = _runner;
        var token = cts.Token;

        try
        {
            // The runner starts on a thread-pool thread: the uninstall's synchronous
            // opening work (state load, hooks up to their first await) must not hold
            // the dispatcher, or the Progress screen never renders. (R95)
            var outcome = await Task.Run(() => runner(progress, token)).ConfigureAwait(true);
            if (outcome.NotInstalled)
            {
                // Nothing to remove — not a failure. (R107)
                ShowNotInstalled();
            }
            else if (outcome.IsBlocked)
            {
                // The gate refused before the replay: nothing was removed. Name the
                // applications and let the user clear them. (R103)
                var described = new List<string>(outcome.Blockers.Count);
                foreach (var b in outcome.Blockers)
                {
                    described.Add(b.Describe());
                }
                PublishBlockers(described);
                CurrentStep = UninstallStep.CloseApps;
            }
            else if (outcome.Success)
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
