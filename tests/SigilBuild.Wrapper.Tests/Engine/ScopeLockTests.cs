// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using System;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Cli;
using SigilBuild.Wrapper.Engine;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Engine;

/// <summary>
/// The session facts the wizard seeds its scope radios from: the resolved scope, and
/// whether the run replaces an install in that scope and so locks the radios. (R116)
/// </summary>
public class ScopeLockTests
{
    private static WrapperBlob Blob(InstallScope scope) => new(
        AppId: "com.example.Locked",
        Parameters: Array.Empty<ParameterDefinition>(),
        InstallSteps: Array.Empty<InstallStep>(),
        PreInstall: Array.Empty<InstallStep>(),
        PostInstall: Array.Empty<InstallStep>(),
        UpdateSteps: Array.Empty<InstallStep>(),
        Scope: scope,
        Version: "2.0.0");

    private static UpgradeState Installed(string version, InstallScope scope) => new(
        Found: true,
        InstalledVersion: version,
        PriorInstallDir: @"C:\Somewhere\Locked",
        PriorUninstallExe: string.Empty,
        FoundScope: scope);

    private static InstallSession Session(UpgradeState state, params string[] args)
    {
        var blob = Blob(InstallScope.Auto);
        return InstallSession.ForTesting(blob, CommandLineParser.Parse(args, blob.Parameters), state);
    }

    [Fact]
    public void A_fresh_install_leaves_the_radios_free()
    {
        // Arrange
        var session = Session(UpgradeState.None);

        // Act
        var locked = session.ScopeLockedByExistingInstall;

        // Assert
        locked.Should().BeFalse();
        session.ResolvedScope.Should().Be(InstallScope.User);
    }

    [Theory]
    [InlineData(InstallScope.User)]
    [InlineData(InstallScope.Machine)]
    public void An_upgrade_locks_the_radios_to_the_existing_installs_scope(InstallScope found)
    {
        // Arrange
        var session = Session(Installed("1.0.0", found));

        // Act
        var locked = session.ScopeLockedByExistingInstall;

        // Assert
        session.ResolvedScope.Should().Be(found, "the existing install's scope wins over an auto scope");
        locked.Should().BeTrue();
    }

    [Theory]
    [InlineData("2.0.0")] // same version: reinstall
    [InlineData("3.0.0")] // newer: a forced downgrade replaces it
    public void A_reinstall_or_forced_downgrade_locks_them_too(string installed)
    {
        // Arrange
        var session = Session(Installed(installed, InstallScope.Machine), "/force-downgrade");

        // Act
        var locked = session.ScopeLockedByExistingInstall;

        // Assert
        locked.Should().BeTrue();
    }

    [Fact]
    public void A_blocked_downgrade_does_not_lock_anything()
    {
        // Arrange — nothing is replaced; the wizard shows the notice screen instead.
        var session = Session(Installed("3.0.0", InstallScope.Machine));

        // Act
        var locked = session.ScopeLockedByExistingInstall;

        // Assert
        locked.Should().BeFalse();
    }

    [Fact]
    public void An_explicit_scope_flag_over_an_install_in_the_other_scope_is_not_locked()
    {
        // Arrange — /currentuser over a machine install is a scope change the operator
        // asked for: the old install is removed and a fresh one lands per-user.
        var session = Session(Installed("1.0.0", InstallScope.Machine), "/currentuser");

        // Act
        var locked = session.ScopeLockedByExistingInstall;

        // Assert
        session.ResolvedScope.Should().Be(InstallScope.User);
        locked.Should().BeFalse();
    }
}
