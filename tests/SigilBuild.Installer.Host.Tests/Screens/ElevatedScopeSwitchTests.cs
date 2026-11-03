// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using SigilBuild.Installer.Host.Branding;
using SigilBuild.Installer.Host.ViewModels;
using Xunit;

namespace SigilBuild.Installer.Host.Tests.Screens;

/// <summary>
/// The scope radios of an elevated wizard: they re-scope the session in-process with
/// no prompt and no handoff, and "Just for me" is unavailable when the elevation
/// belongs to another account. (R118)
/// </summary>
public sealed class ElevatedScopeSwitchTests
{
    private static readonly string UserPath = Path.Combine(Path.GetTempPath(), "sigil-elev-user");
    private static readonly string MachinePath = Path.Combine(Path.GetTempPath(), "sigil-elev-machine");

    private static InstallerViewModel ElevatedAtDestination(bool isMachine, List<bool> switches, bool accept = true)
    {
        var vm = new InstallerViewModel(new BrandTokens { AppName = "Example" });
        vm.Next(); // Welcome → destination
        vm.ConfigureDestination(
            scopeSelectable: true,
            defaultPathResolver: m => m ? MachinePath : UserPath,
            initialPath: isMachine ? MachinePath : UserPath,
            isMachineScope: isMachine);
        vm.ConfigureScopeSwitch(m => { switches.Add(m); return accept; });
        return vm;
    }

    [Fact]
    public void The_radio_switches_freely_with_no_handoff_and_no_shield()
    {
        // Arrange
        var switches = new List<bool>();
        var vm = ElevatedAtDestination(isMachine: false, switches);

        // Act
        vm.IsMachineScope = true;
        var shield = vm.NextNeedsElevation;
        vm.IsUserScope = true;

        // Assert
        switches.Should().Equal(true, false);
        shield.Should().BeFalse("an elevated session asks Windows for nothing");
        vm.IsMachineScope.Should().BeFalse();
        vm.InstallPath.Should().Be(UserPath);
    }

    [Fact]
    public void Next_after_switching_advances_in_this_window()
    {
        // Arrange
        var vm = ElevatedAtDestination(isMachine: false, new List<bool>());
        vm.IsMachineScope = true;

        // Act
        vm.Next();

        // Assert
        vm.CurrentStep.Should().NotBe(InstallerStep.InstallOptions);
    }

    [Fact]
    public void A_refused_switch_keeps_the_selection_and_says_so()
    {
        // Arrange
        var vm = ElevatedAtDestination(isMachine: false, new List<bool>(), accept: false);

        // Act
        vm.IsMachineScope = true;

        // Assert
        vm.IsMachineScope.Should().BeFalse();
        vm.InstallPath.Should().Be(UserPath);
        vm.HasInstallPathError.Should().BeTrue();
    }

    [Fact]
    public void Over_the_shoulder_disables_just_for_me()
    {
        // Arrange
        var switches = new List<bool>();
        var vm = ElevatedAtDestination(isMachine: true, switches);

        // Act
        vm.BlockUserScope();
        vm.IsUserScope = true;

        // Assert
        vm.IsMachineScope.Should().BeTrue();
        vm.UserScopeAvailable.Should().BeFalse();
        vm.CanChooseUserScope.Should().BeFalse();
        vm.UserScopeBlockedHint.Should().Contain("Just for me");
        switches.Should().BeEmpty("the switch is never attempted");
    }

    [Fact]
    public void Blocking_just_for_me_selects_all_users()
    {
        // Arrange
        var vm = ElevatedAtDestination(isMachine: false, new List<bool>());

        // Act
        vm.BlockUserScope();

        // Assert
        vm.IsMachineScope.Should().BeTrue();
    }

    [Fact]
    public void The_same_account_keeps_both_options()
    {
        // Arrange + Act
        var vm = ElevatedAtDestination(isMachine: false, new List<bool>());

        // Assert
        vm.UserScopeAvailable.Should().BeTrue();
        vm.CanChooseUserScope.Should().BeTrue();
        vm.UserScopeBlockedHint.Should().BeEmpty();
    }
}
