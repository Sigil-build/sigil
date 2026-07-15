using System;
using System.Collections.Generic;
using FluentAssertions;
using SigilBuild.Installer.Host.Branding;
using SigilBuild.Installer.Host.ViewModels;
using Xunit;

namespace SigilBuild.Installer.Host.Tests;

/// <summary>
/// P6 (gap G7): the wizard's Close-applications gate. The screen appears only when
/// blockers are found; Retry / Close-for-me re-scan and continue once clear.
/// </summary>
public sealed class CloseAppsGateTests
{
    private static InstallerViewModel Vm() => new(new BrandTokens { AppName = "Acme" });

    [Fact]
    public void Unwired_probe_never_blocks()
    {
        var vm = Vm();
        vm.RescanBlockers().Should().BeTrue("an unwired probe (unit/dev run) is transparent");
        vm.HasBlockers.Should().BeFalse();
    }

    [Fact]
    public void Rescan_publishes_the_blocker_list()
    {
        var vm = Vm();
        vm.ConfigureBlockerProbe(_ => new[] { "Acme Studio (pid 42)", "helper.exe (pid 7)" }, _ => { });

        vm.RescanBlockers().Should().BeFalse();
        vm.HasBlockers.Should().BeTrue();
        vm.Blockers.Should().Equal("Acme Studio (pid 42)", "helper.exe (pid 7)");
    }

    [Fact]
    public void Retry_after_the_user_closes_them_clears_the_gate()
    {
        var vm = Vm();
        var blockers = new List<string> { "Acme Studio (pid 42)" };
        vm.ConfigureBlockerProbe(_ => blockers.ToArray(), _ => { });

        vm.RescanBlockers().Should().BeFalse();

        blockers.Clear(); // the user closed the app
        vm.RescanBlockers().Should().BeTrue();
        vm.HasBlockers.Should().BeFalse();
    }

    [Fact]
    public void Close_for_me_invokes_the_restart_manager_and_clears()
    {
        var vm = Vm();
        var blockers = new List<string> { "Acme Studio (pid 42)" };
        var closeCalled = false;
        vm.ConfigureBlockerProbe(
            scan: _ => blockers.ToArray(),
            close: _ => { closeCalled = true; blockers.Clear(); });

        vm.RescanBlockers().Should().BeFalse();

        vm.CloseBlockingApps();

        closeCalled.Should().BeTrue();
        vm.HasBlockers.Should().BeFalse("the Restart Manager closed them");
    }

    [Fact]
    public void Close_for_me_that_cannot_clear_leaves_the_gate_up()
    {
        var vm = Vm();
        // A declared app_mutex holder the Restart Manager cannot close.
        vm.ConfigureBlockerProbe(_ => new[] { "Global\\AcmeStudio (application mutex held)" }, _ => { });

        vm.RescanBlockers().Should().BeFalse();
        vm.CloseBlockingApps();

        vm.HasBlockers.Should().BeTrue("a mutex holder survives RmShutdown — the gate must stay up");
        vm.CurrentStep.Should().NotBe(InstallerStep.Installing);
    }
}
