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
    public async Task Close_for_me_invokes_the_restart_manager_and_clears()
    {
        var vm = Vm();
        var blockers = new List<string> { "Acme Studio (pid 42)" };
        var closeCalled = false;
        vm.ConfigureBlockerProbe(
            scan: _ => blockers.ToArray(),
            close: _ => { closeCalled = true; blockers.Clear(); });

        vm.RescanBlockers().Should().BeFalse();

        await vm.CloseBlockingAppsAsync();

        closeCalled.Should().BeTrue();
        vm.HasBlockers.Should().BeFalse("the Restart Manager closed them");
        vm.CloseAppsFailed.Should().BeFalse();
    }

    [Fact]
    public async Task Close_for_me_that_cannot_clear_leaves_the_gate_up()
    {
        var vm = Vm();
        // A declared app_mutex holder the Restart Manager cannot close.
        vm.ConfigureBlockerProbe(_ => new[] { "Global\\AcmeStudio (application mutex held)" }, _ => { });

        vm.RescanBlockers().Should().BeFalse();
        await vm.CloseBlockingAppsAsync();

        vm.HasBlockers.Should().BeTrue("a mutex holder survives RmShutdown — the gate must stay up");
        vm.CurrentStep.Should().NotBe(InstallerStep.Installing);
    }

    [Fact]
    public async Task Close_for_me_runs_off_the_calling_thread_and_shows_a_busy_state()
    {
        // Arrange — a close that blocks the way RmShutdown does while it waits on
        // the applications. (R94)
        var vm = Vm();
        var blockers = new List<string> { "dictum-gui.exe (pid 42)" };
        using var started = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var callerThread = Environment.CurrentManagedThreadId;
        int? closeThread = null;
        vm.ConfigureBlockerProbe(
            scan: _ => blockers.ToArray(),
            close: _ =>
            {
                closeThread = Environment.CurrentManagedThreadId;
                started.Set();
                release.Wait(TimeSpan.FromSeconds(10));
                blockers.Clear();
            });
        vm.CurrentStep = InstallerStep.CloseApps;
        vm.RescanBlockers().Should().BeFalse();

        // Act
        var closing = vm.CloseBlockingAppsAsync();

        // Assert — the call returned while the close is still blocked.
        closing.IsCompleted.Should().BeFalse("the close must not run on the calling (UI) thread");
        vm.IsClosingApps.Should().BeTrue();
        vm.CanActOnBlockers.Should().BeFalse("Retry and Close-for-me are disabled while closing");
        vm.CanGoNext.Should().BeFalse();

        // Blocking here keeps this thread out of the pool, so the close can only
        // have started on another one.
        started.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).Should().BeTrue("the close must start without the caller");
        release.Set();
        await closing;

        closeThread.Should().NotBe(callerThread);
        vm.IsClosingApps.Should().BeFalse();
        vm.CanActOnBlockers.Should().BeTrue();
    }

    [Fact]
    public async Task Close_for_me_that_leaves_apps_running_says_so_and_shows_the_fresh_list()
    {
        // Arrange — a tray app's daemon is respawned under a new pid by the close. (R94)
        var vm = Vm();
        var blockers = new List<string> { "dictum-gui.exe (pid 42)", "dictum.exe (pid 43)" };
        vm.ConfigureBlockerProbe(
            scan: _ => blockers.ToArray(),
            close: _ =>
            {
                blockers.Clear();
                blockers.Add("dictum.exe (pid 77)");
            });
        vm.CurrentStep = InstallerStep.CloseApps;
        vm.RescanBlockers().Should().BeFalse();

        // Act
        await vm.CloseBlockingAppsAsync();

        // Assert
        vm.CloseAppsFailed.Should().BeTrue("the screen must say the close did not work");
        vm.Blockers.Should().Equal("dictum.exe (pid 77)");
        vm.CurrentStep.Should().Be(InstallerStep.CloseApps);
    }

    [Fact]
    public async Task Retry_clears_the_could_not_close_notice()
    {
        var vm = Vm();
        var blockers = new List<string> { "Acme Studio (pid 42)" };
        vm.ConfigureBlockerProbe(_ => blockers.ToArray(), _ => { });
        vm.CurrentStep = InstallerStep.CloseApps;
        await vm.CloseBlockingAppsAsync();
        vm.CloseAppsFailed.Should().BeTrue();

        vm.RetryBlockers();

        vm.CloseAppsFailed.Should().BeFalse();
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
        using var started = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var callerThread = Environment.CurrentManagedThreadId;
        int? hookThread = null;
        vm.ConfigurePreInstallHooks((_, ct) =>
        {
            hookThread = Environment.CurrentManagedThreadId;
            started.Set();
            release.Wait(TimeSpan.FromSeconds(10), ct);
            return Task.FromResult(true);
        });

        vm.Next();
        vm.Next();

        vm.PreInstallGateTask!.IsCompleted.Should().BeFalse();
        vm.IsPreparing.Should().BeTrue();
        vm.CanGoNext.Should().BeFalse();
        vm.CanGoBack.Should().BeFalse();

        started.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).Should().BeTrue("the hooks must start without the caller");
        release.Set();
        await vm.PreInstallGateTask!;

        hookThread.Should().NotBe(callerThread);
        vm.IsPreparing.Should().BeFalse();
    }
}
