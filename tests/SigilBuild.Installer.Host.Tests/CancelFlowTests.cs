using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Installer.Host.Branding;
using SigilBuild.Installer.Host.ViewModels;
using Xunit;

namespace SigilBuild.Installer.Host.Tests;

/// <summary>
/// VM-level state-machine tests for the cancel flow.
/// All tests operate on the ViewModel only — no Avalonia window required.
/// </summary>
public sealed class CancelFlowTests
{
    // ── Pre-install screens: cancel is immediate, no modal ───────────────────

    [Fact]
    public async Task CancelAsync_FromWelcome_SetsUserCancelledWithoutModal()
    {
        var vm = new InstallerViewModel(new BrandTokens());
        vm.CurrentStep.Should().Be(InstallerStep.Welcome);

        var result = await vm.CancelAsync(confirmAsync: null);

        result.Should().BeTrue("caller should close the window");
        vm.OutcomeCode.Should().Be(InstallerOutcomeCode.UserCancelled);
    }

    [Fact]
    public async Task CancelAsync_FromLicense_SetsUserCancelled()
    {
        var vm = new InstallerViewModel(new BrandTokens());
        vm.CurrentStep = InstallerStep.License;

        var result = await vm.CancelAsync(confirmAsync: null);

        result.Should().BeTrue();
        vm.OutcomeCode.Should().Be(InstallerOutcomeCode.UserCancelled);
    }

    [Fact]
    public async Task CancelAsync_FromInstallOptions_SetsUserCancelled()
    {
        var vm = new InstallerViewModel(new BrandTokens());
        vm.CurrentStep = InstallerStep.InstallOptions;

        var result = await vm.CancelAsync(confirmAsync: null);

        result.Should().BeTrue();
        vm.OutcomeCode.Should().Be(InstallerOutcomeCode.UserCancelled);
    }

    // ── Installing screen: modal confirmation required ───────────────────────

    [Fact]
    public async Task CancelAsync_FromInstalling_UserConfirms_CancelsEngineAndSets1602()
    {
        var vm = new InstallerViewModel(new BrandTokens());
        vm.CurrentStep = InstallerStep.Installing;

        using var cts = new CancellationTokenSource();
        vm.SetEngineCts(cts);

        var result = await vm.CancelAsync(confirmAsync: () => Task.FromResult(true));

        result.Should().BeTrue("user confirmed");
        cts.IsCancellationRequested.Should().BeTrue("engine CTS must be cancelled");
        vm.OutcomeCode.Should().Be(InstallerOutcomeCode.UserCancelled);
    }

    [Fact]
    public async Task CancelAsync_FromInstalling_UserDeclines_DoesNotCancel()
    {
        var vm = new InstallerViewModel(new BrandTokens());
        vm.CurrentStep = InstallerStep.Installing;

        using var cts = new CancellationTokenSource();
        vm.SetEngineCts(cts);

        var result = await vm.CancelAsync(confirmAsync: () => Task.FromResult(false));

        result.Should().BeFalse("user declined");
        cts.IsCancellationRequested.Should().BeFalse("engine must keep running");
        vm.OutcomeCode.Should().Be(InstallerOutcomeCode.Completed, "outcome must not change");
    }

    // ── Finish screen: cancel is a no-op ─────────────────────────────────────

    [Fact]
    public async Task CancelAsync_FromFinish_IsNoOp_OutcomeRemainsCompleted()
    {
        var vm = new InstallerViewModel(new BrandTokens());
        vm.CurrentStep = InstallerStep.Finish;

        var result = await vm.CancelAsync(confirmAsync: null);

        result.Should().BeFalse("Finish screen has no cancel");
        vm.OutcomeCode.Should().Be(InstallerOutcomeCode.Completed);
    }

    // ── CanCancel property ────────────────────────────────────────────────────

    [Theory]
    [InlineData(InstallerStep.Welcome, true)]
    [InlineData(InstallerStep.License, true)]
    [InlineData(InstallerStep.InstallOptions, true)]
    [InlineData(InstallerStep.Installing, true)]
    [InlineData(InstallerStep.Finish, false)]
    public void CanCancel_ReflectsCurrentStep(InstallerStep step, bool expected)
    {
        var vm = new InstallerViewModel(new BrandTokens());
        vm.CurrentStep = step;
        vm.CanCancel.Should().Be(expected);
    }
}
