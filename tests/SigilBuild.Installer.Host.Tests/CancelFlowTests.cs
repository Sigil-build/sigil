// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using System.Collections.Generic;
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

    // ── The confirmation is mandatory, not only mid-install ──────────────────

    [Theory]
    [InlineData(InstallerStep.Welcome)]
    [InlineData(InstallerStep.License)]
    [InlineData(InstallerStep.InstallOptions)]
    [InlineData(InstallerStep.Options)]
    public async Task CancelAsync_asks_before_abandoning_setup_on_any_pre_install_screen(InstallerStep step)
    {
        // Arrange — cancelling used to be silent everywhere except during the
        // install itself, so Cancel (and the title bar's X, a few pixels from the
        // buttons the user aimed at) discarded a chosen destination, ticked
        // components or a typed licence key with no prompt at all.
        var vm = new InstallerViewModel(new BrandTokens()) { CurrentStep = step };
        var asked = 0;

        // Act — decline.
        var closed = await vm.CancelAsync(() => { asked++; return Task.FromResult(false); });

        // Assert
        asked.Should().Be(1, "the user must be asked on the {0} screen", step);
        closed.Should().BeFalse("declining the prompt keeps the wizard open");
        vm.OutcomeCode.Should().NotBe(InstallerOutcomeCode.UserCancelled,
            "a declined prompt must not record a cancellation that did not happen");
    }

    [Fact]
    public async Task CancelAsync_confirmed_on_a_pre_install_screen_closes_and_records_the_cancellation()
    {
        var vm = new InstallerViewModel(new BrandTokens()) { CurrentStep = InstallerStep.Welcome };

        var closed = await vm.CancelAsync(() => Task.FromResult(true));

        closed.Should().BeTrue();
        vm.OutcomeCode.Should().Be(InstallerOutcomeCode.UserCancelled);
    }

    // ── IsTerminal property — what drives the footer's Close button ───────────

    [Theory]
    [InlineData(InstallerStep.Welcome, false)]
    [InlineData(InstallerStep.License, false)]
    [InlineData(InstallerStep.InstallOptions, false)]
    [InlineData(InstallerStep.Installing, false)]
    [InlineData(InstallerStep.Finish, true)]
    [InlineData(InstallerStep.Failed, true)]
    [InlineData(InstallerStep.DowngradeBlocked, true)]
    public void IsTerminal_ReflectsCurrentStep(InstallerStep step, bool expected)
    {
        var vm = new InstallerViewModel(new BrandTokens());
        vm.CurrentStep = step;
        vm.IsTerminal.Should().Be(expected);
    }

    [Fact]
    public void IsTerminal_raises_a_change_notification_so_the_footer_swaps()
    {
        // Arrange — the footer's two halves bind to IsTerminal and !IsTerminal. If
        // the step change does not announce it, the wizard reaches the Finish screen
        // still showing Back / Next / Cancel, which is the state that trapped users:
        // the only enabled button was Cancel, and CancelAsync returns false there.
        var vm = new InstallerViewModel(new BrandTokens());
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        // Act
        vm.CurrentStep = InstallerStep.Finish;

        // Assert
        raised.Should().Contain(nameof(InstallerViewModel.IsTerminal));
    }
}
