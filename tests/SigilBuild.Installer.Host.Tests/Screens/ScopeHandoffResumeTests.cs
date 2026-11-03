// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using System.IO;
using FluentAssertions;
using SigilBuild.Installer.Host.Branding;
using SigilBuild.Installer.Host.ViewModels;
using SigilBuild.Wrapper.Engine;
using Xunit;

namespace SigilBuild.Installer.Host.Tests.Screens;

/// <summary>
/// Both sides of the machine-scope handoff as the wizard sees them: the unelevated
/// wizard hands over the destination on screen, and the elevated one opens where the
/// user left off instead of on Welcome. (R117)
/// </summary>
public sealed class ScopeHandoffResumeTests
{
    private static readonly string UserPath = Path.Combine(Path.GetTempPath(), "sigil-resume-user");
    private static readonly string MachinePath = Path.Combine(Path.GetTempPath(), "sigil-resume-machine");

    /// <summary>The elevated instance as App wires it: machine scope seeded, the forwarded path pre-filled.</summary>
    private static InstallerViewModel ElevatedChild(bool withLicense, string forwardedPath)
    {
        var vm = new InstallerViewModel(new BrandTokens { AppName = "Example" });
        if (withLicense)
        {
            vm.LoadLicense("Example EULA text.");
        }
        vm.ConfigureDestination(
            scopeSelectable: true,
            defaultPathResolver: m => m ? MachinePath : UserPath,
            initialPath: forwardedPath,
            isMachineScope: true);
        return vm;
    }

    [Fact]
    public void The_elevated_wizard_resumes_on_License_with_machine_scope_and_the_path()
    {
        // Arrange
        var forwarded = Path.Combine(MachinePath, "Typed");
        var vm = ElevatedChild(withLicense: true, forwarded);

        // Act
        vm.ResumeAfterMachineScopeHandoff(pastDestination: true);

        // Assert
        vm.CurrentStep.Should().Be(InstallerStep.License,
            "the user already went through Welcome and Location; acceptance is asked again by the process that installs");
        vm.LicenseAccepted.Should().BeFalse();
        vm.IsMachineScope.Should().BeTrue();
        vm.InstallPath.Should().Be(forwarded);
        vm.RailSteps[0].State.Should().Be(RailStepState.Done);
        vm.RailSteps[1].State.Should().Be(RailStepState.Done);
        vm.RailSteps[2].State.Should().Be(RailStepState.Current);
    }

    [Fact]
    public void Back_from_the_resumed_screen_reaches_the_destination()
    {
        // Arrange
        var vm = ElevatedChild(withLicense: true, MachinePath);
        vm.ResumeAfterMachineScopeHandoff(pastDestination: true);

        // Act
        vm.Back();

        // Assert
        vm.CurrentStep.Should().Be(InstallerStep.InstallOptions);
        vm.IsMachineScope.Should().BeTrue();
    }

    [Fact]
    public void When_Installing_comes_next_the_wizard_resumes_on_the_destination_and_does_not_start()
    {
        // Arrange — no License, Options or declared screens.
        var vm = ElevatedChild(withLicense: false, MachinePath);

        // Act
        vm.ResumeAfterMachineScopeHandoff(pastDestination: true);

        // Assert
        vm.CurrentStep.Should().Be(InstallerStep.InstallOptions,
            "an install starts from a Next pressed in the window that performs it");
        vm.InstallTask.Should().BeNull();
        vm.IsMachineScope.Should().BeTrue();
    }

    [Fact]
    public void A_refused_destination_resumes_on_the_destination_screen()
    {
        // Arrange
        var vm = ElevatedChild(withLicense: true, MachinePath);

        // Act
        vm.ResumeAfterMachineScopeHandoff(pastDestination: false);

        // Assert
        vm.CurrentStep.Should().Be(InstallerStep.InstallOptions,
            "the pre-fill fell back to the scope default, and the user must see that");
    }

    [Fact]
    public void A_blocked_downgrade_keeps_its_notice()
    {
        // Arrange
        var vm = ElevatedChild(withLicense: true, MachinePath);
        vm.SetUpgradeState(UpgradeAction.DowngradeBlocked, "9.0.0");

        // Act
        vm.ResumeAfterMachineScopeHandoff(pastDestination: true);

        // Assert
        vm.CurrentStep.Should().Be(InstallerStep.DowngradeBlocked);
    }

    // ── the unelevated side ───────────────────────────────────────────────────

    private static InstallerViewModel UnelevatedAtDestination()
    {
        var vm = new InstallerViewModel(new BrandTokens { AppName = "Example" });
        vm.Next(); // Welcome → destination
        vm.ConfigureDestination(
            scopeSelectable: true,
            defaultPathResolver: m => m ? MachinePath : UserPath,
            initialPath: UserPath);
        return vm;
    }

    [Fact]
    public void The_handoff_receives_the_destination_on_screen()
    {
        // Arrange
        string? handedOver = null;
        var vm = UnelevatedAtDestination();
        vm.ConfigureMachineScopeElevation(path => { handedOver = path; return true; }, _ => true);
        vm.IsMachineScope = true;
        var typed = Path.Combine(MachinePath, "Typed");
        vm.InstallPath = "  " + typed + "  ";

        // Act
        vm.Next();

        // Assert
        handedOver.Should().Be(typed);
    }

    [Fact]
    public void A_destination_machine_containment_refuses_is_refused_before_the_prompt()
    {
        // Arrange
        var prompts = 0;
        var vm = UnelevatedAtDestination();
        vm.ConfigureMachineScopeElevation(_ => { prompts++; return true; }, _ => false);
        vm.IsMachineScope = true;

        // Act
        vm.Next();

        // Assert
        prompts.Should().Be(0);
        vm.CurrentStep.Should().Be(InstallerStep.InstallOptions);
        vm.InstallPathError.Should().Contain("all users");
    }
}
