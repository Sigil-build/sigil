using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using SigilBuild.Installer.Host.Branding;

namespace SigilBuild.Installer.Host.ViewModels;

public enum InstallerStep { Welcome, License, InstallOptions, Installing, Finish, Custom }

/// <summary>Windows MSI-convention exit codes surfaced by the installer process.</summary>
public enum InstallerOutcomeCode
{
    Completed    = 0,
    UserCancelled = 1602,
    Failed       = 1603,
}

public sealed class InstallerViewModel : INotifyPropertyChanged
{
    private InstallerStep _step = InstallerStep.Welcome;
    private string _installPath;
    private CancellationTokenSource? _engineCts;

    public InstallerViewModel(BrandTokens tokens)
    {
        Brand = tokens;
        _installPath = System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles),
            tokens.AppName);
    }

    public BrandTokens Brand { get; }

    public InstallerOutcomeCode OutcomeCode { get; private set; } = InstallerOutcomeCode.Completed;

    public InstallerStep CurrentStep
    {
        get => _step;
        set { if (_step != value) { _step = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanGoBack)); OnPropertyChanged(nameof(CanGoNext)); OnPropertyChanged(nameof(CanCancel)); } }
    }

    public bool CanGoBack => _step is not InstallerStep.Welcome and not InstallerStep.Installing and not InstallerStep.Finish;
    public bool CanGoNext => _step is not InstallerStep.Installing and not InstallerStep.Finish;

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

    public void Next() => CurrentStep = _step switch
    {
        InstallerStep.Welcome => InstallerStep.License,
        InstallerStep.License => LicenseAccepted ? InstallerStep.InstallOptions : _step,
        InstallerStep.InstallOptions => InstallerStep.Installing,
        InstallerStep.Installing => InstallerStep.Finish,
        _ => _step,
    };

    public void Back() => CurrentStep = _step switch
    {
        InstallerStep.License => InstallerStep.Welcome,
        InstallerStep.InstallOptions => InstallerStep.License,
        InstallerStep.Installing => _step,
        _ => _step,
    };

    /// <summary>
    /// Registers the <see cref="CancellationTokenSource"/> owned by the install operation so
    /// <see cref="CancelAsync"/> can signal it when the user cancels during installation.
    /// </summary>
    public void SetEngineCts(CancellationTokenSource cts) => _engineCts = cts;

    /// <summary>
    /// Attempts to cancel the installation.  Returns <c>true</c> when the caller should
    /// close the window; <c>false</c> when the user dismissed the confirmation dialog.
    /// On the Finish screen this is always a no-op (returns <c>false</c>).
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
