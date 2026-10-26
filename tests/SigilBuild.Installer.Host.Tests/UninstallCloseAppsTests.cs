// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using SigilBuild.Installer.Host.Branding;
using SigilBuild.Installer.Host.ViewModels;
using SigilBuild.Installer.Host.Views;
using SigilBuild.Installer.Host.Views.Screens;
using SigilBuild.Wrapper.Core.Localization;
using SigilBuild.Wrapper.Engine;
using Xunit;

namespace SigilBuild.Installer.Host.Tests;

/// <summary>
/// The uninstall wizard's Close-applications step. The session refuses before the
/// journal replay when applications hold the install, and the wizard shows the
/// install wizard's Close-applications screen: Retry re-scans, Close for me closes
/// off the UI thread, Cancel leaves with nothing removed. Clearing the blockers
/// invokes the runner again, which resumes the uninstall. (R103)
/// </summary>
public sealed class UninstallCloseAppsTests
{
    private static readonly AppBlocker Daemon = new("dictum.exe", 43, FromMutex: false);

    private static InstallOutcome Blocked(params AppBlocker[] blockers)
        => new(false, "these applications are using files") { Blockers = blockers };

    /// <summary>A runner that reports blocked on its first call and succeeds after.</summary>
    private static (UninstallViewModel Vm, Func<int> Calls) BlockedThenClear()
    {
        var vm = new UninstallViewModel(new BrandTokens { AppName = "Dictum" });
        var calls = 0;
        vm.ConfigureRunner((_, _) =>
        {
            var n = Interlocked.Increment(ref calls);
            return Task.FromResult(n == 1 ? Blocked(Daemon) : new InstallOutcome(true, null));
        });
        return (vm, () => Volatile.Read(ref calls));
    }

    [Fact]
    public async Task A_blocked_outcome_shows_the_close_apps_step_with_name_and_pid()
    {
        // Arrange
        var (vm, calls) = BlockedThenClear();

        // Act
        vm.Confirm();
        await vm.UninstallTask!;

        // Assert
        vm.CurrentStep.Should().Be(UninstallStep.CloseApps);
        vm.IsCloseApps.Should().BeTrue();
        vm.IsCancellable.Should().BeTrue();
        vm.Blockers.Should().Equal("dictum.exe (pid 43)");
        vm.HasBlockers.Should().BeTrue();
        vm.OutcomeCode.Should().NotBe(InstallerOutcomeCode.Failed, "a blocked run is not a failed one");
        calls().Should().Be(1);
    }

    [Fact]
    public async Task Retry_while_still_blocked_stays_with_the_refreshed_list()
    {
        // Arrange
        var (vm, calls) = BlockedThenClear();
        vm.ConfigureBlockerProbe(() => new[] { "dictum.exe (pid 77)" }, () => { });
        vm.Confirm();
        await vm.UninstallTask!;

        // Act
        vm.RetryBlockers();

        // Assert
        vm.CurrentStep.Should().Be(UninstallStep.CloseApps);
        vm.Blockers.Should().Equal("dictum.exe (pid 77)");
        calls().Should().Be(1, "the runner is not resumed while something is still running");
    }

    [Fact]
    public async Task Retry_once_clear_resumes_the_uninstall_to_done()
    {
        // Arrange
        var (vm, calls) = BlockedThenClear();
        var running = new List<string> { "dictum.exe (pid 43)" };
        vm.ConfigureBlockerProbe(() => running.ToArray(), () => { });
        vm.Confirm();
        await vm.UninstallTask!;

        // Act — the user closed the application themselves.
        running.Clear();
        vm.RetryBlockers();
        await vm.UninstallTask!;

        // Assert
        calls().Should().Be(2, "Retry resumes the run through the same runner");
        vm.CurrentStep.Should().Be(UninstallStep.Done);
        vm.OutcomeCode.Should().Be(InstallerOutcomeCode.Completed);
    }

    [Fact]
    public async Task A_resumed_run_that_is_blocked_again_returns_to_the_close_apps_step()
    {
        // Arrange — an app that restarted between the Retry scan and the session's gate.
        var vm = new UninstallViewModel(new BrandTokens());
        vm.ConfigureRunner((_, _) => Task.FromResult(Blocked(Daemon)));
        vm.ConfigureBlockerProbe(() => Array.Empty<string>(), () => { });
        vm.Confirm();
        await vm.UninstallTask!;

        // Act
        vm.RetryBlockers();
        await vm.UninstallTask!;

        // Assert
        vm.CurrentStep.Should().Be(UninstallStep.CloseApps);
        vm.Blockers.Should().Equal("dictum.exe (pid 43)");
    }

    [Fact]
    public async Task Close_for_me_runs_off_the_calling_thread_with_a_busy_state_then_resumes()
    {
        // Arrange — a close that blocks the way RmShutdown does while it waits.
        var (vm, calls) = BlockedThenClear();
        var running = new List<string> { "dictum.exe (pid 43)" };
        using var started = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        int? closeThread = null;
        vm.ConfigureBlockerProbe(
            scan: () => running.ToArray(),
            close: () =>
            {
                closeThread = Environment.CurrentManagedThreadId;
                started.Set();
                release.Wait(TimeSpan.FromSeconds(10));
                running.Clear();
            });
        vm.Confirm();
        await vm.UninstallTask!;
        vm.CurrentStep.Should().Be(UninstallStep.CloseApps);

        // Act — the caller's thread is read here, after the await above moved this
        // test onto whichever pool thread finished the first run.
        var callerThread = Environment.CurrentManagedThreadId;
        var closing = vm.CloseBlockingAppsAsync();

        // Assert — the call returned while the close is still blocked.
        closing.IsCompleted.Should().BeFalse("the close must not run on the calling (UI) thread");
        vm.IsClosingApps.Should().BeTrue();
        vm.CanActOnBlockers.Should().BeFalse("Retry and Close-for-me are disabled while closing");

        // Blocking here keeps this thread out of the pool, so the close can only
        // have started on another one.
        started.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)
            .Should().BeTrue("the close must start without the caller");
        release.Set();
        await closing;
        await vm.UninstallTask!;

        closeThread.Should().NotBe(callerThread);
        vm.IsClosingApps.Should().BeFalse();
        vm.CanActOnBlockers.Should().BeTrue();
        vm.CloseAppsFailed.Should().BeFalse();
        calls().Should().Be(2);
        vm.CurrentStep.Should().Be(UninstallStep.Done);
    }

    [Fact]
    public async Task Close_for_me_that_leaves_apps_running_says_so_and_does_not_resume()
    {
        // Arrange — the daemon is respawned under a new pid by the close.
        var (vm, calls) = BlockedThenClear();
        var running = new List<string> { "dictum.exe (pid 43)" };
        vm.ConfigureBlockerProbe(
            scan: () => running.ToArray(),
            close: () =>
            {
                running.Clear();
                running.Add("dictum.exe (pid 77)");
            });
        vm.Confirm();
        await vm.UninstallTask!;

        // Act
        await vm.CloseBlockingAppsAsync();

        // Assert
        vm.CloseAppsFailed.Should().BeTrue("the screen must say Setup could not close them");
        vm.Blockers.Should().Equal("dictum.exe (pid 77)");
        vm.CurrentStep.Should().Be(UninstallStep.CloseApps);
        calls().Should().Be(1);

        // And Retry clears the notice.
        vm.RetryBlockers();
        vm.CloseAppsFailed.Should().BeFalse();
    }

    [Fact]
    public async Task Cancel_on_the_close_apps_step_exits_2_with_nothing_removed()
    {
        // Arrange
        var (vm, calls) = BlockedThenClear();
        vm.Confirm();
        await vm.UninstallTask!;

        // Act
        vm.Cancel();

        // Assert
        vm.OutcomeCode.Should().Be(InstallerOutcomeCode.UserCancelled);
        ((int)vm.OutcomeCode).Should().Be(2);
        calls().Should().Be(1, "cancelling never resumes the replay");
    }

    [Fact]
    public void Cancel_is_a_no_op_while_the_uninstall_runs()
    {
        var vm = new UninstallViewModel(new BrandTokens()) { CurrentStep = UninstallStep.Progress };

        vm.Cancel();

        vm.IsCancellable.Should().BeFalse();
        vm.OutcomeCode.Should().Be(InstallerOutcomeCode.Completed);
    }

    [AvaloniaFact]
    public async Task The_uninstall_window_shows_the_shared_close_apps_screen_with_cancel()
    {
        // Arrange
        var (vm, _) = BlockedThenClear();
        var window = new UninstallWindow { DataContext = vm };
        window.Show();

        // Act
        vm.Confirm();
        await vm.UninstallTask!;

        Dispatcher.UIThread.RunJobs();

        // Assert
        var screen = window.GetVisualDescendants().OfType<CloseAppsView>().Single();
        screen.IsVisible.Should().BeTrue();
        screen.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text)
            .Should().Contain("dictum.exe (pid 43)");

        var footer = window.GetVisualDescendants().OfType<Button>()
            .Where(b => b.IsEffectivelyVisible)
            .Select(b => b.Content as string)
            .ToList();
        footer.Should().Contain(S.NavCancel);
        footer.Should().Contain(S.NavRetry);
        footer.Should().Contain(S.CloseAppsCloseForMe);
        footer.Should().NotContain(S.UninstallAction, "Uninstall belongs to the Confirm step only");
    }

    [Fact]
    public void The_close_apps_explanation_says_removing_on_uninstall_and_updating_on_install()
    {
        // Arrange
        var uninstall = new UninstallViewModel(new BrandTokens());
        var install = new InstallerViewModel(new BrandTokens());

        // Act
        var uninstallBody = uninstall.CloseAppsBody;
        var installBody = install.CloseAppsBody;

        // Assert
        uninstallBody.Should().Be(S.CloseAppsUninstallBody);
        installBody.Should().Be(S.CloseAppsBody);
        uninstallBody.Should().NotBe(installBody, "an uninstall does not update files");
    }
}
