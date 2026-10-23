// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using System;
using System.IO;
using FluentAssertions;
using SigilBuild.Installer.Host.Branding;
using SigilBuild.Installer.Host.ViewModels;
using Xunit;

namespace SigilBuild.Installer.Host.Tests.Screens;

/// <summary>
/// VM-level coverage for the Destination screen: path validation gating Next
/// inline, and the scope toggle recomputing the default path.
/// </summary>
public sealed class DestinationScreenTests
{
    private static InstallerViewModel AtDestination()
    {
        var vm = new InstallerViewModel(new BrandTokens { AppName = "Example" });
        vm.Next(); // Welcome → InstallOptions (destination)
        vm.CurrentStep.Should().Be(InstallerStep.InstallOptions);
        return vm;
    }

    [Fact]
    public void Relative_path_blocks_next_with_inline_error()
    {
        var vm = AtDestination();
        vm.InstallPath = Path.Combine("relative", "path");

        vm.Next();

        vm.CurrentStep.Should().Be(InstallerStep.InstallOptions, "an invalid path must block advancing");
        vm.HasInstallPathError.Should().BeTrue();
        vm.InstallPathError.Should().Contain("absolute");
    }

    [Fact]
    public void Blank_path_blocks_next()
    {
        var vm = AtDestination();
        vm.InstallPath = "   ";

        vm.ValidateDestination().Should().BeFalse();
        vm.HasInstallPathError.Should().BeTrue();
    }

    [Fact]
    public void A_file_path_is_rejected()
    {
        var file = Path.Combine(Path.GetTempPath(), "sigil-t13-" + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(file, "x");
        try
        {
            var vm = AtDestination();
            vm.InstallPath = file;

            vm.ValidateDestination().Should().BeFalse();
            vm.InstallPathError.Should().Contain("file");
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Valid_absolute_writable_path_clears_the_error_and_advances()
    {
        var vm = AtDestination();
        vm.InstallPath = Path.Combine(Path.GetTempPath(), "SigilT13", Guid.NewGuid().ToString("N"));

        vm.Next();

        vm.HasInstallPathError.Should().BeFalse();
        vm.CurrentStep.Should().Be(InstallerStep.Installing);
    }

    [Fact]
    public void Scope_toggle_recomputes_the_default_path()
    {
        var vm = new InstallerViewModel(new BrandTokens { AppName = "Example" });
        var userPath = Path.Combine("C:", "Users", "me", "Example");
        var machinePath = Path.Combine("C:", "Program Files", "Example");

        vm.ConfigureDestination(
            scopeSelectable: true,
            defaultPathResolver: isMachine => isMachine ? machinePath : userPath,
            initialPath: userPath);

        vm.ScopeSelectable.Should().BeTrue();
        vm.InstallPath.Should().Be(userPath);
        vm.IsUserScope.Should().BeTrue();

        vm.IsMachineScope = true;

        vm.InstallPath.Should().Be(machinePath, "selecting all-users swaps the path to Program Files");
        vm.IsUserScope.Should().BeFalse();

        vm.IsUserScope = true;
        vm.InstallPath.Should().Be(userPath);
        vm.IsMachineScope.Should().BeFalse();
    }

    // ── R90: the machine-scope option hands off to an elevated run ────────────
    //
    // Scope is fixed when the session is created, before the window exists: it
    // selects the containment roots install_dir is checked against and whether the
    // process is elevated. The radio could only ever swap the displayed path, so
    // picking "All users" on an unelevated run used to walk the user all the way to
    // a containment refusal — "outside the user scope root" — with nothing
    // installed and no mention of administrator rights. Found on a clean VM by
    // clicking the first choice the wizard offers.

    // Real, writable paths: ValidateDestination gates Next on the destination being
    // usable, so a made-up path would block advancing for a reason that has nothing
    // to do with what these tests are about.
    private static readonly string UserPath = Path.Combine(Path.GetTempPath(), "sigil-scope-user");
    private static readonly string MachinePath = Path.Combine(Path.GetTempPath(), "sigil-scope-machine");

    private static InstallerViewModel AtDestinationWithScopes(Func<bool>? elevator)
    {
        var vm = AtDestination();
        vm.ConfigureDestination(
            scopeSelectable: true,
            defaultPathResolver: isMachine => isMachine ? MachinePath : UserPath,
            initialPath: UserPath);
        if (elevator is not null)
        {
            vm.ConfigureMachineScopeElevation(elevator);
        }
        return vm;
    }

    [Fact]
    public void Selecting_a_scope_only_swaps_the_path_and_never_prompts()
    {
        var handoffs = 0;
        var vm = AtDestinationWithScopes(() => { handoffs++; return true; });

        vm.IsMachineScope = true;

        handoffs.Should().Be(0,
            "picking a scope is a choice, not a commitment — the user must still be able to " +
            "see and edit the destination before anything asks for administrator rights");
        vm.IsMachineScope.Should().BeTrue();
        vm.InstallPath.Should().Be(MachinePath);
    }

    [Fact]
    public void Next_hands_off_to_an_elevated_run_and_leaves_this_one_alone()
    {
        var handoffs = 0;
        var vm = AtDestinationWithScopes(() => { handoffs++; return true; });
        vm.IsMachineScope = true;

        vm.Next();

        handoffs.Should().Be(1, "the choice is honoured by starting an elevated run, not by this one");
        vm.CurrentStep.Should().Be(InstallerStep.InstallOptions,
            "this process is standing down; advancing its own wizard would be a second install");
    }

    [Fact]
    public void A_declined_elevation_stays_on_the_screen_and_says_why()
    {
        var vm = AtDestinationWithScopes(() => false);
        vm.IsMachineScope = true;

        vm.Next();

        vm.CurrentStep.Should().Be(InstallerStep.InstallOptions);
        vm.HasInstallPathError.Should().BeTrue("a Next that quietly does nothing is the worse failure");
        vm.InstallPathError.Should().Contain("administrator");
    }

    [Fact]
    public void User_scope_advances_without_any_prompt()
    {
        var handoffs = 0;
        var vm = AtDestinationWithScopes(() => { handoffs++; return true; });

        vm.Next();

        handoffs.Should().Be(0, "installing just for me needs no administrator, and must not ask for one");
        vm.CurrentStep.Should().NotBe(InstallerStep.InstallOptions);
    }

    [Fact]
    public void The_shield_marks_exactly_the_click_that_will_prompt()
    {
        var vm = AtDestinationWithScopes(() => true);
        vm.NextNeedsElevation.Should().BeFalse("user scope is selected — Next elevates nothing");

        vm.IsMachineScope = true;
        vm.NextNeedsElevation.Should().BeTrue();

        vm.IsUserScope = true;
        vm.NextNeedsElevation.Should().BeFalse("the shield must disappear with the reason for it");

        AtDestinationWithScopes(elevator: null).Also(v => v.IsMachineScope = true)
            .NextNeedsElevation.Should().BeFalse(
                "a session already in machine scope needs no relaunch, so Next raises no prompt");
    }

    [Fact]
    public void Without_a_handoff_the_toggle_still_swaps_the_path()
    {
        // The unwired path is what a machine-scoped session uses.
        var vm = AtDestinationWithScopes(elevator: null);

        vm.IsMachineScope = true;

        vm.IsMachineScope.Should().BeTrue();
        vm.InstallPath.Should().Be(MachinePath);
    }
}

internal static class VmTestExtensions
{
    /// <summary>Applies <paramref name="act"/> and returns the same instance, so a
    /// two-step arrangement stays one expression.</summary>
    public static T Also<T>(this T value, Action<T> act)
    {
        act(value);
        return value;
    }
}
