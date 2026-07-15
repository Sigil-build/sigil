using FluentAssertions;
using SigilBuild.Installer.Host.Branding;
using SigilBuild.Installer.Host.ViewModels;
using Xunit;

namespace SigilBuild.Installer.Host.Tests;

/// <summary>
/// P2 (gap G4): the Done screen's "Launch &lt;App&gt;" checkbox. It appears only
/// when the manifest declares run_after_install, is checked by default, and the
/// app launches on close only when completed + checked.
/// </summary>
public sealed class FinishLaunchTests
{
    [Fact]
    public void Checkbox_hidden_and_no_launch_without_run_after_install()
    {
        var vm = new InstallerViewModel(new BrandTokens());
        var launched = false;
        vm.ConfigureLaunch(hasRunAfterInstall: false, "x", () => launched = true);

        vm.HasRunAfterInstall.Should().BeFalse();

        vm.LaunchIfRequested();
        launched.Should().BeFalse();
    }

    [Fact]
    public void Launches_on_close_when_completed_and_checked()
    {
        var vm = new InstallerViewModel(new BrandTokens());
        var launched = false;
        vm.ConfigureLaunch(hasRunAfterInstall: true, "Launch Acme Studio", () => launched = true);

        vm.HasRunAfterInstall.Should().BeTrue();
        vm.LaunchLabel.Should().Be("Launch Acme Studio");
        vm.LaunchAfterInstall.Should().BeTrue("the checkbox is checked by default");
        vm.OutcomeCode.Should().Be(InstallerOutcomeCode.Completed);

        vm.LaunchIfRequested();
        launched.Should().BeTrue();
    }

    [Fact]
    public void Does_not_launch_when_the_box_is_unchecked()
    {
        var vm = new InstallerViewModel(new BrandTokens());
        var launched = false;
        vm.ConfigureLaunch(hasRunAfterInstall: true, "Launch X", () => launched = true);

        vm.LaunchAfterInstall = false;
        vm.LaunchIfRequested();

        launched.Should().BeFalse();
    }
}
