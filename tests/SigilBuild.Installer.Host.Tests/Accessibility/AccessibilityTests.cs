using Avalonia.Headless.XUnit;
using FluentAssertions;
using SigilBuild.Installer.Host.Branding;
using SigilBuild.Installer.Host.ViewModels;
using Xunit;

namespace SigilBuild.Installer.Host.Tests.Accessibility;

/// <summary>
/// Keyboard-navigation tests: verifies that every step is reachable and no
/// dead-ends exist in the wizard flow. These run entirely via ViewModel logic
/// (no real keyboard events) so they are fast and fully headless.
/// </summary>
public class AccessibilityTests
{
    [AvaloniaFact]
    public void Navigation_CanProgressThroughAllSteps_Without_DeadEnd()
    {
        var vm = new InstallerViewModel(new BrandTokens());

        // Welcome → License
        vm.CurrentStep.Should().Be(InstallerStep.Welcome);
        vm.CanGoNext.Should().BeTrue();
        vm.Next();

        // License — must accept before proceeding
        vm.CurrentStep.Should().Be(InstallerStep.License);
        vm.CanGoNext.Should().BeTrue();   // button is enabled; the guard lives in Next()
        vm.LicenseAccepted = true;
        vm.Next();

        // InstallOptions → Installing
        vm.CurrentStep.Should().Be(InstallerStep.InstallOptions);
        vm.CanGoNext.Should().BeTrue();
        vm.Next();

        // Installing — back and next are both locked during installation
        vm.CurrentStep.Should().Be(InstallerStep.Installing);
        vm.CanGoBack.Should().BeFalse();
        vm.CanGoNext.Should().BeFalse();

        // Simulate install completion (direct step advance; the real engine
        // advances here via InstallSession.RunInstallAsync — see InstallFlowTests).
        vm.CurrentStep = InstallerStep.Finish;
        vm.CanGoBack.Should().BeFalse();
        vm.CanGoNext.Should().BeFalse();
    }

    [AvaloniaFact]
    public void Navigation_BackTraversal_ReachesWelcome()
    {
        var vm = new InstallerViewModel(new BrandTokens());
        vm.LicenseAccepted = true;

        // Walk forward to InstallOptions
        vm.Next(); // Welcome → License
        vm.Next(); // License → InstallOptions

        vm.CurrentStep.Should().Be(InstallerStep.InstallOptions);
        vm.CanGoBack.Should().BeTrue();

        vm.Back(); // InstallOptions → License
        vm.CurrentStep.Should().Be(InstallerStep.License);
        vm.CanGoBack.Should().BeTrue();

        vm.Back(); // License → Welcome
        vm.CurrentStep.Should().Be(InstallerStep.Welcome);
        vm.CanGoBack.Should().BeFalse();
    }
}
