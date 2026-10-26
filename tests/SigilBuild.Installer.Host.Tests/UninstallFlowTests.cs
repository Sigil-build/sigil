// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Installer.Host.Branding;
using SigilBuild.Installer.Host.ViewModels;
using SigilBuild.Wrapper.Engine;
using Xunit;

namespace SigilBuild.Installer.Host.Tests;

/// <summary>
/// VM-level tests for the interactive uninstall flow: the branded
/// confirm → progress → done sequence driving the real
/// <see cref="InstallSession.RunUninstallInteractiveAsync"/> (injected here as a
/// fake so the tests stay fast + headless). A failure lands on the Failed screen;
/// dismissing the confirm records a user-cancelled outcome.
/// </summary>
public sealed class UninstallFlowTests
{
    [Fact]
    public void Fresh_view_model_starts_on_the_confirm_screen()
    {
        var vm = new UninstallViewModel(new BrandTokens { AppName = "Acme Studio" });

        vm.CurrentStep.Should().Be(UninstallStep.Confirm);
        vm.IsConfirm.Should().BeTrue();
        vm.ConfirmTitle.Should().Be("Uninstall Acme Studio");
        vm.ConfirmMessage.Should().Contain("Acme Studio");
    }

    [Fact]
    public async Task Confirm_advances_confirm_to_progress_to_done_on_success()
    {
        var vm = new UninstallViewModel(new BrandTokens { AppName = "Acme Studio" });
        vm.ConfigureRunner((progress, ct) =>
        {
            progress.Report(new StepProgress(1, 2, "unlink Desktop\\Acme Studio.lnk", false));
            progress.Report(new StepProgress(2, 2, "delete C:\\Acme\\uninstall.exe", false));
            return Task.FromResult(new InstallOutcome(true, null));
        });

        vm.Confirm(); // Confirm → Progress (fires the engine)
        await vm.UninstallTask!;

        vm.CurrentStep.Should().Be(UninstallStep.Done);
        vm.IsDone.Should().BeTrue();
        vm.OutcomeCode.Should().Be(InstallerOutcomeCode.Completed);
        ((int)vm.OutcomeCode).Should().Be(0);
        vm.DoneMessage.Should().Be("Acme Studio was removed");
        await WaitUntilAsync(() => vm.LogLines.Count >= 2);
        vm.LogLines.Select(l => l.Text).Should().Contain("unlink Desktop\\Acme Studio.lnk");
    }

    [Fact]
    public async Task Failure_advances_to_failed_screen_with_error_and_exit_1()
    {
        var vm = new UninstallViewModel(new BrandTokens());
        vm.ConfigureRunner((progress, ct) =>
            Task.FromResult(new InstallOutcome(false, "no uninstall state found")));

        vm.Confirm();
        await vm.UninstallTask!;

        vm.CurrentStep.Should().Be(UninstallStep.Failed);
        vm.IsFailed.Should().BeTrue();
        vm.ErrorMessage.Should().Contain("no uninstall state found");
        vm.OutcomeCode.Should().Be(InstallerOutcomeCode.Failed);
        ((int)vm.OutcomeCode).Should().Be(1);
    }

    /// <summary>
    /// A leftover <c>uninstall.exe</c> opens on a neutral not-installed screen, not on
    /// the confirmation, and leaves with exit 0 — whether through Close or the window's
    /// close gesture. (R107)
    /// </summary>
    [Fact]
    public void An_app_that_is_not_installed_shows_the_not_installed_screen_and_exits_0()
    {
        // Arrange
        var vm = new UninstallViewModel(new BrandTokens { AppName = "Acme Studio" });

        // Act
        vm.ShowNotInstalled();
        vm.Cancel(); // the window's close gesture calls this on every step

        // Assert
        vm.CurrentStep.Should().Be(UninstallStep.NotInstalled);
        vm.IsNotInstalled.Should().BeTrue();
        vm.IsFinished.Should().BeTrue("the footer offers a single Close");
        vm.IsCancellable.Should().BeFalse();
        vm.NotInstalledMessage.Should().Be("Acme Studio is not installed");
        vm.OutcomeCode.Should().Be(InstallerOutcomeCode.Completed);
        ((int)vm.OutcomeCode).Should().Be(0);
    }

    /// <summary>
    /// The state can also disappear between the window opening and the user confirming;
    /// the session then reports not-installed rather than a failure, and the window lands
    /// on the same neutral screen. (R107)
    /// </summary>
    [Fact]
    public async Task A_not_installed_outcome_lands_on_the_not_installed_screen_not_failed()
    {
        // Arrange
        var vm = new UninstallViewModel(new BrandTokens { AppName = "Acme Studio" });
        vm.ConfigureRunner((progress, ct) =>
            Task.FromResult(new InstallOutcome(false, "Acme Studio is not installed") { NotInstalled = true }));

        // Act
        vm.Confirm();
        await vm.UninstallTask!;

        // Assert
        vm.CurrentStep.Should().Be(UninstallStep.NotInstalled);
        vm.IsFailed.Should().BeFalse();
        vm.ErrorMessage.Should().BeNull();
        ((int)vm.OutcomeCode).Should().Be(0);
    }

    [Fact]
    public async Task Uninstall_runner_runs_off_the_calling_thread_so_the_progress_screen_can_render()
    {
        // Arrange — synchronous opening work, as the real session does before its
        // first await. (R95)
        var vm = new UninstallViewModel(new BrandTokens());
        using var started = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var callerThread = Environment.CurrentManagedThreadId;
        int? runnerThread = null;
        vm.ConfigureRunner((progress, ct) =>
        {
            runnerThread = Environment.CurrentManagedThreadId;
            started.Set();
            release.Wait(TimeSpan.FromSeconds(10), ct);
            return Task.FromResult(new InstallOutcome(true, null));
        });

        // Act
        vm.Confirm();

        // Assert
        vm.CurrentStep.Should().Be(UninstallStep.Progress);
        vm.UninstallTask!.IsCompleted.Should().BeFalse(
            "Confirm must return before the runner's synchronous work, not run it inline");

        started.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).Should().BeTrue("the runner must start without the caller");
        release.Set();
        await vm.UninstallTask!;

        runnerThread.Should().NotBe(callerThread);
        vm.CurrentStep.Should().Be(UninstallStep.Done);
    }

    [Fact]
    public void Cancel_on_confirm_records_user_cancelled_exit_2()
    {
        var vm = new UninstallViewModel(new BrandTokens());
        vm.CurrentStep.Should().Be(UninstallStep.Confirm);

        vm.Cancel();

        vm.OutcomeCode.Should().Be(InstallerOutcomeCode.UserCancelled);
        ((int)vm.OutcomeCode).Should().Be(2);
    }

    [Fact]
    public void Confirm_is_a_no_op_off_the_confirm_screen()
    {
        var vm = new UninstallViewModel(new BrandTokens()) { CurrentStep = UninstallStep.Done };

        vm.Confirm();

        vm.UninstallTask.Should().BeNull("Confirm only starts the engine from the Confirm screen");
        vm.CurrentStep.Should().Be(UninstallStep.Done);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(10).ConfigureAwait(false);
        }
    }
}
