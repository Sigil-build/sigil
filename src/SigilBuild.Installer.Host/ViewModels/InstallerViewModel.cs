using System.ComponentModel;
using System.Runtime.CompilerServices;
using SigilBuild.Installer.Host.Branding;

namespace SigilBuild.Installer.Host.ViewModels;

public enum InstallerStep { Welcome, License, InstallOptions, Installing, Finish, Custom }

public sealed class InstallerViewModel : INotifyPropertyChanged
{
    private InstallerStep _step = InstallerStep.Welcome;
    private string _installPath;

    public InstallerViewModel(BrandTokens tokens)
    {
        Brand = tokens;
        _installPath = System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles),
            tokens.AppName);
    }

    public BrandTokens Brand { get; }

    public InstallerStep CurrentStep
    {
        get => _step;
        set { if (_step != value) { _step = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanGoBack)); OnPropertyChanged(nameof(CanGoNext)); } }
    }

    public bool CanGoBack => _step is not InstallerStep.Welcome and not InstallerStep.Installing and not InstallerStep.Finish;
    public bool CanGoNext => _step is not InstallerStep.Installing and not InstallerStep.Finish;

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

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name ?? ""));
}
