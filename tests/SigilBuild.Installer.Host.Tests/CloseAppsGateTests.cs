// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Installer.Host.Branding;
using SigilBuild.Installer.Host.ViewModels;
using Xunit;

namespace SigilBuild.Installer.Host.Tests;

/// <summary>
/// The wizard's Close-applications gate. The screen appears only when
/// blockers are found; Retry / Close-for-me re-scan and continue once clear. (gap G7)
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

    [Fact]
    public async Task Pre_install_hooks_run_before_the_scan_and_can_clear_the_gate()
    {
        // Arrange — the publisher's --quit hook stops the app the scan would report. (R93)
        var vm = Vm();
        var blockers = new List<string> { "dictum-gui.exe (pid 42)" };
        var order = new List<string>();
        vm.ConfigureBlockerProbe(_ => { order.Add("scan"); return blockers.ToArray(); }, _ => { });
        vm.ConfigurePreInstallHooks((_, _) =>
        {
            order.Add("hooks");
            blockers.Clear();
            return Task.FromResult(true);
        });

        // Act
        vm.Next(); // Welcome → InstallOptions
        vm.Next(); // InstallOptions → hooks → scan → Installing
        await vm.PreInstallGateTask!;

        // Assert
        order.Should().Equal("hooks", "scan");
        vm.CurrentStep.Should().Be(InstallerStep.Installing, "the hook cleared the blocker");
    }

    [Fact]
    public async Task Pre_install_hooks_run_at_most_once_per_wizard_run()
    {
        var vm = Vm();
        var hookRuns = 0;
        vm.ConfigureBlockerProbe(_ => new[] { "Acme Studio (pid 42)" }, _ => { });
        vm.ConfigurePreInstallHooks((_, _) =>
        {
            hookRuns++;
            return Task.FromResult(true);
        });

        vm.Next(); // Welcome → InstallOptions
        vm.Next(); // hooks → scan → CloseApps
        await vm.PreInstallGateTask!;
        vm.CurrentStep.Should().Be(InstallerStep.CloseApps);

        vm.Back(); // CloseApps → Welcome (the cursor never left InstallOptions)
        vm.Next(); // Welcome → InstallOptions
        vm.Next(); // straight to the scan this time

        hookRuns.Should().Be(1);
        vm.CurrentStep.Should().Be(InstallerStep.CloseApps);
    }

    [Fact]
    public async Task A_failed_pre_install_hook_skips_the_scan_and_lets_the_engine_report_it()
    {
        var vm = Vm();
        var scanned = false;
        vm.ConfigureBlockerProbe(_ => { scanned = true; return new[] { "Acme Studio (pid 42)" }; }, _ => { });
        vm.ConfigurePreInstallHooks((_, _) => Task.FromResult(false));

        vm.Next();
        vm.Next();
        await vm.PreInstallGateTask!;

        scanned.Should().BeFalse();
        vm.CurrentStep.Should().Be(InstallerStep.Installing);
    }

    [Fact]
    public async Task Pre_install_hooks_run_off_the_calling_thread_with_next_disabled()
    {
        var vm = Vm();
        using var release = new ManualResetEventSlim(false);
        var callerThread = Environment.CurrentManagedThreadId;
        int? hookThread = null;
        vm.ConfigurePreInstallHooks((_, ct) =>
        {
            hookThread = Environment.CurrentManagedThreadId;
            release.Wait(TimeSpan.FromSeconds(10), ct);
            return Task.FromResult(true);
        });

        vm.Next();
        vm.Next();

        vm.PreInstallGateTask!.IsCompleted.Should().BeFalse();
        vm.IsPreparing.Should().BeTrue();
        vm.CanGoNext.Should().BeFalse();
        vm.CanGoBack.Should().BeFalse();

        release.Set();
        await vm.PreInstallGateTask!;

        hookThread.Should().NotBe(callerThread);
        vm.IsPreparing.Should().BeFalse();
    }
}
