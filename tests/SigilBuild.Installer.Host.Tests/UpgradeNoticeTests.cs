using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Installer.Host.Branding;
using SigilBuild.Installer.Host.ViewModels;
using SigilBuild.Wrapper.Engine;
using Xunit;

namespace SigilBuild.Installer.Host.Tests;

/// <summary>
/// P3 (gap G3) host coverage: the wizard shows an "Upgrading from x.y.z" banner on an
/// upgrade, and routes a blocked downgrade to a terminal notice screen carrying the
/// dedicated exit code (3).
/// </summary>
public sealed class UpgradeNoticeTests
{
    private static InstallerViewModel Vm(string appVersion = "2.0.0") =>
        new(new BrandTokens { AppName = "Acme Studio", AppVersion = appVersion });

    [Fact]
    public void No_upgrade_notice_by_default()
    {
        var vm = Vm();
        vm.HasUpgradeNotice.Should().BeFalse();
        vm.UpgradeNotice.Should().BeEmpty();
    }

    [Fact]
    public void Upgrade_shows_upgrading_from_banner_with_both_versions()
    {
        var vm = Vm(appVersion: "2.0.0");

        vm.SetUpgradeState(UpgradeAction.Upgrade, "1.0.0");

        vm.HasUpgradeNotice.Should().BeTrue();
        vm.UpgradeNotice.Should().Contain("Upgrading");
        vm.UpgradeNotice.Should().Contain("Acme Studio");
        vm.UpgradeNotice.Should().Contain("1.0.0");
        vm.UpgradeNotice.Should().Contain("2.0.0");
        // An upgrade is not a block: the flow proceeds normally.
        vm.CurrentStep.Should().Be(InstallerStep.Welcome);
    }

    [Fact]
    public void Same_version_shows_no_upgrade_banner()
    {
        var vm = Vm();
        vm.SetUpgradeState(UpgradeAction.Same, "2.0.0");
        vm.HasUpgradeNotice.Should().BeFalse();
    }

    [Fact]
    public void Blocked_downgrade_routes_to_notice_screen_with_exit_code_3()
    {
        var vm = Vm(appVersion: "1.0.0");

        vm.SetUpgradeState(UpgradeAction.DowngradeBlocked, "2.0.0");

        vm.CurrentStep.Should().Be(InstallerStep.DowngradeBlocked);
        vm.OutcomeCode.Should().Be(InstallerOutcomeCode.DowngradeBlocked);
        ((int)vm.OutcomeCode).Should().Be(3);
        vm.CanGoNext.Should().BeFalse();
        vm.CanGoBack.Should().BeFalse();
        vm.DowngradeBlockedMessage.Should().Contain("2.0.0");
        vm.DowngradeBlockedMessage.Should().Contain("Acme Studio");
    }

    [Fact]
    public async Task Closing_the_block_screen_keeps_exit_code_3()
    {
        var vm = Vm(appVersion: "1.0.0");
        vm.SetUpgradeState(UpgradeAction.DowngradeBlocked, "2.0.0");

        var closed = await vm.CancelAsync(confirmAsync: null);

        closed.Should().BeTrue("the block screen's button closes the window");
        vm.OutcomeCode.Should().Be(InstallerOutcomeCode.DowngradeBlocked, "closing a blocked downgrade must stay exit 3, not become cancel/2");
    }
}
