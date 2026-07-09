using FluentAssertions;
using SigilBuild.Installer.Host.Branding;
using SigilBuild.Installer.Host.ViewModels;
using Xunit;

namespace SigilBuild.Installer.Host.Tests;

/// <summary>
/// T10 host coverage: the wizard surfaces a repair/reinstall notice when the
/// session reports a prior install of the app. The v1 behaviour is uninstall-then-
/// install (performed by the engine); the view-model flag only informs the user.
/// </summary>
public sealed class ReinstallNoticeTests
{
    [Fact]
    public void No_notice_by_default()
    {
        var vm = new InstallerViewModel(new BrandTokens());

        vm.ExistingInstallDetected.Should().BeFalse();
        vm.ReinstallNotice.Should().BeEmpty();
    }

    [Fact]
    public void Notice_appears_when_existing_install_is_reported()
    {
        var vm = new InstallerViewModel(new BrandTokens { AppName = "Acme Studio" });

        vm.SetExistingInstall(true);

        vm.ExistingInstallDetected.Should().BeTrue();
        vm.ReinstallNotice.Should().Contain("Acme Studio");
        vm.ReinstallNotice.Should().Contain("reinstall");
    }

    [Fact]
    public void Notice_clears_when_reset()
    {
        var vm = new InstallerViewModel(new BrandTokens { AppName = "Acme Studio" });
        vm.SetExistingInstall(true);

        vm.SetExistingInstall(false);

        vm.ExistingInstallDetected.Should().BeFalse();
        vm.ReinstallNotice.Should().BeEmpty();
    }
}
