using FluentAssertions;
using SigilBuild.Installer.Host.Branding;
using SigilBuild.Installer.Host.ViewModels;
using Xunit;

namespace SigilBuild.Installer.Host.Tests;

/// <summary>
/// P5 (gap G6) host coverage: the Done screen surfaces a reboot notice when a
/// prerequisite installer reported reboot-required (exit 3010). The flag is set by
/// the host after the install runner completes.
/// </summary>
public sealed class RebootNoticeTests
{
    [Fact]
    public void No_reboot_notice_by_default()
    {
        var vm = new InstallerViewModel(new BrandTokens());
        vm.RebootRequired.Should().BeFalse();
        vm.RebootNotice.Should().BeEmpty();
    }

    [Fact]
    public void Notice_appears_when_reboot_is_reported()
    {
        var vm = new InstallerViewModel(new BrandTokens());

        vm.SetRebootRequired(true);

        vm.RebootRequired.Should().BeTrue();
        vm.RebootNotice.Should().Contain("restart");
    }

    [Fact]
    public void Notice_clears_when_reset()
    {
        var vm = new InstallerViewModel(new BrandTokens());
        vm.SetRebootRequired(true);

        vm.SetRebootRequired(false);

        vm.RebootRequired.Should().BeFalse();
        vm.RebootNotice.Should().BeEmpty();
    }
}
