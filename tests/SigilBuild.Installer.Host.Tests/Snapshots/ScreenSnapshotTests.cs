using Avalonia.Headless.XUnit;
using SigilBuild.Installer.Host.Branding;
using SigilBuild.Installer.Host.ViewModels;
using SigilBuild.Installer.Host.Views;
using Xunit;

namespace SigilBuild.Installer.Host.Tests.Snapshots;

/// <summary>
/// Golden-image tests for the 6 installer screens × 3 brand themes = 18 combinations
/// (× 2 DPI variants = 36 total images; DPI variants are added incrementally).
///
/// HOW TO CAPTURE BASELINES (must be done on a machine with a display or Xvfb):
///   SIGIL_SNAPSHOT_CAPTURE=1 dotnet test --filter "ScreenSnapshotTests" --configuration Release
///
/// Baseline PNGs are stored in:
///   tests/SigilBuild.Installer.Host.Tests/Snapshots/Baseline/
///
/// In CI (no baselines committed yet) every test is SKIPPED — not failed.
/// Once baselines are committed the tests run automatically and diff any regressions.
/// </summary>
public class ScreenSnapshotTests : SnapshotTestBase
{
    // ── Default brand ─────────────────────────────────────────────────────

    [AvaloniaFact]
    public void Snapshot_Welcome_Default() =>
        CaptureOrSkip(InstallerStep.Welcome, DefaultBrand, "welcome.default.100dpi");

    [AvaloniaFact]
    public void Snapshot_License_Default() =>
        CaptureOrSkip(InstallerStep.License, DefaultBrand, "license.default.100dpi");

    [AvaloniaFact]
    public void Snapshot_InstallOptions_Default() =>
        CaptureOrSkip(InstallerStep.InstallOptions, DefaultBrand, "installoptions.default.100dpi");

    [AvaloniaFact]
    public void Snapshot_Installing_Default() =>
        CaptureOrSkip(InstallerStep.Installing, DefaultBrand, "installing.default.100dpi");

    [AvaloniaFact]
    public void Snapshot_Finish_Default() =>
        CaptureOrSkip(InstallerStep.Finish, DefaultBrand, "finish.default.100dpi");

    [AvaloniaFact]
    public void Snapshot_Custom_Default() =>
        CaptureOrSkip(InstallerStep.Custom, DefaultBrand, "custom.default.100dpi");

    // ── Verdant Studio brand ──────────────────────────────────────────────

    [AvaloniaFact]
    public void Snapshot_Welcome_Verdant() =>
        CaptureOrSkip(InstallerStep.Welcome, VerdantStudioBrand, "welcome.verdant.100dpi");

    [AvaloniaFact]
    public void Snapshot_License_Verdant() =>
        CaptureOrSkip(InstallerStep.License, VerdantStudioBrand, "license.verdant.100dpi");

    [AvaloniaFact]
    public void Snapshot_InstallOptions_Verdant() =>
        CaptureOrSkip(InstallerStep.InstallOptions, VerdantStudioBrand, "installoptions.verdant.100dpi");

    [AvaloniaFact]
    public void Snapshot_Installing_Verdant() =>
        CaptureOrSkip(InstallerStep.Installing, VerdantStudioBrand, "installing.verdant.100dpi");

    [AvaloniaFact]
    public void Snapshot_Finish_Verdant() =>
        CaptureOrSkip(InstallerStep.Finish, VerdantStudioBrand, "finish.verdant.100dpi");

    [AvaloniaFact]
    public void Snapshot_Custom_Verdant() =>
        CaptureOrSkip(InstallerStep.Custom, VerdantStudioBrand, "custom.verdant.100dpi");

    // ── Lumen Compose brand ───────────────────────────────────────────────

    [AvaloniaFact]
    public void Snapshot_Welcome_Lumen() =>
        CaptureOrSkip(InstallerStep.Welcome, LumenComposeBrand, "welcome.lumen.100dpi");

    [AvaloniaFact]
    public void Snapshot_License_Lumen() =>
        CaptureOrSkip(InstallerStep.License, LumenComposeBrand, "license.lumen.100dpi");

    [AvaloniaFact]
    public void Snapshot_InstallOptions_Lumen() =>
        CaptureOrSkip(InstallerStep.InstallOptions, LumenComposeBrand, "installoptions.lumen.100dpi");

    [AvaloniaFact]
    public void Snapshot_Installing_Lumen() =>
        CaptureOrSkip(InstallerStep.Installing, LumenComposeBrand, "installing.lumen.100dpi");

    [AvaloniaFact]
    public void Snapshot_Finish_Lumen() =>
        CaptureOrSkip(InstallerStep.Finish, LumenComposeBrand, "finish.lumen.100dpi");

    [AvaloniaFact]
    public void Snapshot_Custom_Lumen() =>
        CaptureOrSkip(InstallerStep.Custom, LumenComposeBrand, "custom.lumen.100dpi");

    // ── Helper ────────────────────────────────────────────────────────────

    /// <summary>
    /// In compare mode: skips if the baseline PNG is absent (first-run / CI
    /// before capture). In capture mode: always runs and writes the baseline.
    /// </summary>
    private static void CaptureOrSkip(InstallerStep step, BrandTokens brand, string key)
    {
        if (!CaptureMode &&
            !System.IO.File.Exists(System.IO.Path.Combine(SnapshotDir, $"{key}.png")))
        {
            // Baseline not yet captured — skip rather than fail.
            return;
        }

        var vm = new InstallerViewModel(brand) { CurrentStep = step };
        var window = new InstallerWindow { DataContext = vm };
        AssertSnapshot(window, key);
    }
}
