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
/// VM-level tests for the real-engine install flow that replaced the throwaway
/// copy-loop <c>InstallerEngine</c>. The engine runner is injected as a fake so
/// these stay fast + headless while exercising the same orchestration the App
/// wires to <see cref="InstallSession.RunInstallAsync"/>.
/// </summary>
public sealed class InstallFlowTests
{
    private static InstallerViewModel ArrangeAtInstalling(
        Func<IProgress<StepProgress>, CancellationToken, Task<InstallOutcome>> runner)
    {
        // No license loaded → the License screen is absent (T14). Decision-4 flow
        // for a default VM: Welcome → InstallOptions (destination) → Installing.
        var vm = new InstallerViewModel(new BrandTokens());
        vm.ConfigureInstallRunner(runner);
        vm.Next(); // Welcome → InstallOptions
        vm.Next(); // InstallOptions → Installing (fires the engine)
        return vm;
    }

    [Fact]
    public async Task Successful_install_advances_to_Finish_with_log_and_exit_0()
    {
        var vm = ArrangeAtInstalling((progress, ct) =>
        {
            progress.Report(new StepProgress(1, 2, "copy bin/app.exe → C:\\App", false));
            progress.Report(new StepProgress(2, 2, "link Desktop\\App.lnk", false));
            return Task.FromResult(new InstallOutcome(true, null));
        });

        await vm.InstallTask!;

        vm.CurrentStep.Should().Be(InstallerStep.Finish);
        vm.OutcomeCode.Should().Be(InstallerOutcomeCode.Completed);
        ((int)vm.OutcomeCode).Should().Be(0);
        await WaitUntilAsync(() => vm.LogLines.Count >= 2);
        vm.LogLines.Select(l => l.Text).Should().Contain("link Desktop\\App.lnk");
    }

    [Fact]
    public async Task Step_failure_drives_Failed_screen_with_error_and_exit_1()
    {
        var vm = ArrangeAtInstalling((progress, ct) =>
        {
            progress.Report(new StepProgress(1, 2, "copy bin/app.exe → C:\\App", false));
            progress.Report(new StepProgress(1, 2, "error: access denied", true));
            progress.Report(new StepProgress(1, 2, "rollback: reverting changes", true));
            return Task.FromResult(new InstallOutcome(false, "install_steps: access denied"));
        });

        await vm.InstallTask!;

        vm.CurrentStep.Should().Be(InstallerStep.Failed);
        vm.ErrorMessage.Should().Contain("access denied");
        vm.OutcomeCode.Should().Be(InstallerOutcomeCode.Failed);
        ((int)vm.OutcomeCode).Should().Be(1);
    }

    [Fact]
    public async Task Cancel_mid_install_signals_engine_and_exits_2()
    {
        var vm = ArrangeAtInstalling((progress, ct) => WaitForCancelAsync(ct));

        vm.CurrentStep.Should().Be(InstallerStep.Installing);

        // Same path as the Cancel button: confirm, then cancel the running engine.
        var closed = await vm.CancelAsync(confirmAsync: () => Task.FromResult(true));
        closed.Should().BeTrue();

        await vm.InstallTask!;

        vm.OutcomeCode.Should().Be(InstallerOutcomeCode.UserCancelled);
        ((int)vm.OutcomeCode).Should().Be(2);
    }

    [Fact]
    public async Task Failed_screen_Cancel_closes_without_downgrading_exit_code()
    {
        var vm = ArrangeAtInstalling((progress, ct) =>
            Task.FromResult(new InstallOutcome(false, "boom")));

        await vm.InstallTask!;
        vm.CurrentStep.Should().Be(InstallerStep.Failed);

        var closed = await vm.CancelAsync(confirmAsync: null);

        closed.Should().BeTrue("the Failed screen's button closes the window");
        vm.OutcomeCode.Should().Be(InstallerOutcomeCode.Failed, "closing a failed install must stay exit 1, not become cancel/2");
    }

    /// <summary>A fake install that blocks until the engine token is cancelled, then throws like the real engine.</summary>
    private static async Task<InstallOutcome> WaitForCancelAsync(CancellationToken ct)
    {
        var tcs = new TaskCompletionSource();
        using var reg = ct.Register(() => tcs.TrySetResult());
        await tcs.Task.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return new InstallOutcome(true, null);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(10).ConfigureAwait(false);
        }
    }
}
