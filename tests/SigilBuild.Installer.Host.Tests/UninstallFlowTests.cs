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
/// VM-level tests for the T15 interactive uninstall flow: the branded
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

    [Fact]
    public void CancelConfirm_records_user_cancelled_exit_2()
    {
        var vm = new UninstallViewModel(new BrandTokens());
        vm.CurrentStep.Should().Be(UninstallStep.Confirm);

        vm.CancelConfirm();

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
