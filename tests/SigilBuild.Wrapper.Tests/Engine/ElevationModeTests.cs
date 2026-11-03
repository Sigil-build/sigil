// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using System;
using System.Text.Json;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Cli;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Json;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Engine;

/// <summary>
/// <c>installer.elevation</c>: when a run elevates at start, the in-process scope
/// switch an elevated wizard uses instead of the machine-scope handoff, and the
/// over-the-shoulder rule that takes "Just for me" away. (R118)
/// </summary>
public class ElevationModeTests
{
    private static WrapperBlob Blob(InstallScope scope, InstallerElevation elevation = InstallerElevation.OnDemand) => new(
        AppId: "com.example.Elevated",
        Parameters: Array.Empty<ParameterDefinition>(),
        InstallSteps: Array.Empty<InstallStep>(),
        PreInstall: Array.Empty<InstallStep>(),
        PostInstall: Array.Empty<InstallStep>(),
        UpdateSteps: Array.Empty<InstallStep>(),
        Scope: scope,
        Version: "2.0.0",
        Elevation: elevation);

    private static InstallSession Session(
        WrapperBlob blob,
        bool elevated,
        bool anotherUser = false,
        Func<string, InstallScope, UpgradeState>? state = null,
        params string[] args) =>
        InstallSession.ForTesting(
            blob,
            CommandLineParser.Parse(args, blob.Parameters),
            state ?? ((_, _) => UpgradeState.None),
            new InstallSession.ElevationFacts(elevated, anotherUser));

    // ── the start-of-run decision table ───────────────────────────────────────

    [Theory]
    // on_demand: only a resolved machine scope elevates.
    [InlineData(InstallerElevation.OnDemand, InstallScope.User, InstallScope.User, false, false)]
    [InlineData(InstallerElevation.OnDemand, InstallScope.Machine, InstallScope.Machine, false, true)]
    [InlineData(InstallerElevation.OnDemand, InstallScope.Auto, InstallScope.User, false, false)]
    [InlineData(InstallerElevation.OnDemand, InstallScope.Auto, InstallScope.Machine, false, true)]
    // on_start: scope: auto elevates in either scope; scope: machine is unchanged.
    [InlineData(InstallerElevation.OnStart, InstallScope.Auto, InstallScope.User, false, true)]
    [InlineData(InstallerElevation.OnStart, InstallScope.Auto, InstallScope.Machine, false, true)]
    [InlineData(InstallerElevation.OnStart, InstallScope.Machine, InstallScope.Machine, false, true)]
    // on_start with scope: user is refused at pack time; were it packed, it still would not elevate.
    [InlineData(InstallerElevation.OnStart, InstallScope.User, InstallScope.User, false, false)]
    // An elevated process never elevates again.
    [InlineData(InstallerElevation.OnDemand, InstallScope.Machine, InstallScope.Machine, true, false)]
    [InlineData(InstallerElevation.OnStart, InstallScope.Auto, InstallScope.User, true, false)]
    [InlineData(InstallerElevation.OnStart, InstallScope.Auto, InstallScope.Machine, true, false)]
    public void The_start_of_run_elevation_decision(
        InstallerElevation elevation, InstallScope manifest, InstallScope resolved, bool elevated, bool expected)
    {
        // Act
        var elevates = InstallSession.ElevatesAtStart(manifest, elevation, resolved, WrapperMode.Install, elevated);

        // Assert
        elevates.Should().Be(expected);
    }

    [Theory]
    [InlineData(WrapperMode.Uninstall)]
    [InlineData(WrapperMode.Update)]
    public void On_start_elevates_install_runs_only(WrapperMode mode)
    {
        // Act — a per-user uninstall.exe carries the same blob and must stay unelevated.
        var elevates = InstallSession.ElevatesAtStart(
            InstallScope.Auto, InstallerElevation.OnStart, InstallScope.User, mode, elevated: false);

        // Assert
        elevates.Should().BeFalse();
    }

    // ── the in-process scope switch ───────────────────────────────────────────

    [Fact]
    public void An_elevated_auto_session_switches_scope_without_a_handoff()
    {
        // Arrange
        var session = Session(Blob(InstallScope.Auto, InstallerElevation.OnStart), elevated: true);

        // Act
        var needsRelaunch = session.MachineScopeNeedsRelaunch;
        var switched = session.SwitchScope(InstallScope.Machine);

        // Assert
        needsRelaunch.Should().BeFalse("an elevated session reaches machine scope without a relaunch");
        session.CanSwitchScopeInProcess.Should().BeTrue();
        switched.Should().BeTrue();
        session.ResolvedScope.Should().Be(InstallScope.Machine);
        session.ResolveDefaultInstallDir().Should().StartWith(ScopeLayout.For(InstallScope.Machine).InstallRoot);

        session.SwitchScope(InstallScope.User).Should().BeTrue("the radio switches freely in both directions");
        session.ResolvedScope.Should().Be(InstallScope.User);
    }

    [Fact]
    public void An_unelevated_session_keeps_the_handoff()
    {
        // Arrange
        var session = Session(Blob(InstallScope.Auto), elevated: false);

        // Act + Assert
        session.CanSwitchScopeInProcess.Should().BeFalse();
        session.MachineScopeNeedsRelaunch.Should().BeTrue();
        session.SwitchScope(InstallScope.Machine).Should().BeFalse();
        session.ResolvedScope.Should().Be(InstallScope.User);
    }

    [Fact]
    public void A_switch_re_reads_the_installed_state_for_the_new_scope()
    {
        // Arrange — a machine install exists; the session starts per-user (explicit flag).
        var state = new Func<string, InstallScope, UpgradeState>((_, scope) => scope == InstallScope.Machine
            ? new UpgradeState(true, "1.0.0", @"C:\Program Files\Elevated", string.Empty, InstallScope.Machine)
            : UpgradeState.None);
        var session = Session(
            Blob(InstallScope.Auto, InstallerElevation.OnStart), elevated: true, state: state, args: new[] { "/currentuser" });
        session.UpgradeAction.Should().Be(UpgradeAction.Fresh);

        // Act
        var switched = session.SwitchScope(InstallScope.Machine);

        // Assert
        switched.Should().BeTrue();
        session.UpgradeAction.Should().Be(UpgradeAction.Upgrade, "machine scope has its own install to upgrade");
        session.InstalledVersion.Should().Be("1.0.0");
        session.ResolveDefaultInstallDir().Should().Be(@"C:\Program Files\Elevated",
            "the upgrade keeps the existing folder (R112)");
    }

    // ── over-the-shoulder elevation ───────────────────────────────────────────

    [Fact]
    public void Over_the_shoulder_resolves_auto_to_machine_and_refuses_the_switch_to_user()
    {
        // Arrange
        var session = Session(Blob(InstallScope.Auto, InstallerElevation.OnStart), elevated: true, anotherUser: true);

        // Act
        var switched = session.SwitchScope(InstallScope.User);

        // Assert
        session.ResolvedScope.Should().Be(InstallScope.Machine,
            "a per-user install would land in the approving administrator's profile");
        session.UserScopeBlockedByElevation.Should().BeTrue();
        switched.Should().BeFalse();
        session.ResolvedScope.Should().Be(InstallScope.Machine);
    }

    [Theory]
    [InlineData("/silent", "/currentuser")]
    [InlineData("/currentuser")]
    public void Over_the_shoulder_refuses_an_explicit_currentuser(params string[] args)
    {
        // Act
        var act = () => Session(
            Blob(InstallScope.Auto, InstallerElevation.OnStart), elevated: true, anotherUser: true, args: args);

        // Assert — UsageException is exit 64 at both entry points.
        act.Should().Throw<UsageException>().WithMessage("*/currentuser*");
    }

    [Fact]
    public void The_same_account_elevating_keeps_both_scopes()
    {
        // Arrange
        var session = Session(
            Blob(InstallScope.Auto, InstallerElevation.OnStart), elevated: true, anotherUser: false,
            args: new[] { "/currentuser" });

        // Act + Assert
        session.ResolvedScope.Should().Be(InstallScope.User);
        session.UserScopeBlockedByElevation.Should().BeFalse();
        session.SwitchScope(InstallScope.Machine).Should().BeTrue();
        session.SwitchScope(InstallScope.User).Should().BeTrue();
    }

    [Fact]
    public void Over_the_shoulder_leaves_an_uninstall_alone()
    {
        // Act — the uninstall path is not where a scope is chosen.
        var session = Session(
            Blob(InstallScope.Auto, InstallerElevation.OnStart), elevated: true, anotherUser: true,
            args: new[] { "/Uninstall", "/currentuser" });

        // Assert
        session.ResolvedScope.Should().Be(InstallScope.User);
    }

    // ── the blob carries the field ────────────────────────────────────────────

    [Fact]
    public void Elevation_roundtrips_through_the_blob_wire_form()
    {
        // Arrange
        var blob = Blob(InstallScope.Auto, InstallerElevation.OnStart);

        // Act
        var json = JsonSerializer.Serialize(
            SerializableWrapperBlob.FromWrapperBlob(blob), WrapperBlobJsonContext.Default.SerializableWrapperBlob);
        var back = SerializableWrapperBlob.ToWrapperBlob(
            JsonSerializer.Deserialize(json, WrapperBlobJsonContext.Default.SerializableWrapperBlob)!);

        // Assert
        json.Should().Contain("\"elevation\":\"OnStart\"");
        back.Elevation.Should().Be(InstallerElevation.OnStart);
    }

    [Fact]
    public void A_blob_without_the_field_is_on_demand()
    {
        // Act
        var back = JsonSerializer.Deserialize(
            "{\"appId\":\"com.example.Old\"}", WrapperBlobJsonContext.Default.SerializableWrapperBlob)!;

        // Assert
        back.Elevation.Should().Be(InstallerElevation.OnDemand);
    }
}
