// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Cli;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Tests.Helpers;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Engine;

/// <summary>
/// The session side of the wizard's machine-scope handoff (R90).
/// </summary>
/// <remarks>
/// Scope is fixed when the session is created — it selects the containment roots
/// <c>install_dir</c> is checked against, and whether the process runs elevated —
/// so the wizard's "All users of this computer" radio could never change it. It
/// only swapped the displayed path, which walked the user to a containment refusal
/// with nothing installed. Honouring the choice means a new session, which means a
/// new process, which means these two members.
/// </remarks>
public class MachineScopeRelaunchTests
{
    private static WrapperBlob BlobWithScope(InstallScope scope) => new(
        AppId: "com.example.Scoped",
        Parameters: Array.Empty<ParameterDefinition>(),
        InstallSteps: Array.Empty<InstallStep>(),
        PreInstall: Array.Empty<InstallStep>(),
        PostInstall: Array.Empty<InstallStep>(),
        UpdateSteps: Array.Empty<InstallStep>(),
        Scope: scope);

    private static InstallSession SessionFor(InstallScope manifestScope, params string[] args)
    {
        var blob = BlobWithScope(manifestScope);
        return InstallSession.ForTesting(blob, CommandLineParser.Parse(args, blob.Parameters));
    }

    [Fact]
    public void A_machine_scoped_session_needs_no_relaunch()
    {
        SessionFor(InstallScope.Machine).MachineScopeNeedsRelaunch.Should().BeFalse(
            "it is already in the scope the radio would ask for");
    }

    [Theory]
    [InlineData(InstallScope.User)]
    [InlineData(InstallScope.Auto)]
    public void Any_other_session_needs_one(InstallScope manifestScope)
    {
        // Auto resolves to user scope with no prior install, so both of these are
        // sessions whose containment roots are the user's. An elevated process in
        // this state still needs the relaunch: elevation is not the condition,
        // resolved scope is.
        SessionFor(manifestScope).MachineScopeNeedsRelaunch.Should().BeTrue();
    }

    [Fact]
    public void The_relaunch_adds_allusers()
    {
        var args = SessionFor(InstallScope.Auto).BuildMachineScopeRelaunchArgs(Array.Empty<string>());

        args.Should().Equal("/allusers", InstallSession.ScopeHandoffSwitch);
    }

    [Fact]
    public void The_relaunch_replaces_an_explicit_currentuser_rather_than_arguing_with_it()
    {
        var args = SessionFor(InstallScope.Auto, "/currentuser")
            .BuildMachineScopeRelaunchArgs(new[] { "/currentuser" });

        args.Should().NotContain("/currentuser",
            "handing the child both flags would leave it a contradiction to resolve");
        args.Should().Contain("/allusers");
    }

    [Fact]
    public void The_relaunch_does_not_duplicate_allusers()
    {
        var args = SessionFor(InstallScope.Machine).BuildMachineScopeRelaunchArgs(new[] { "/allusers" });

        args.Should().ContainSingle(a => a == "/allusers");
    }

    [Fact]
    public void Everything_else_the_user_passed_is_carried_over()
    {
        // The child is a fresh run of the same install, so it has to inherit the
        // original invocation — a log destination, a language choice — with only the
        // scope flag rewritten.
        var original = new[] { "/LOG=C:\\logs\\setup.log", "/lang=uk", "/currentuser" };

        var args = SessionFor(InstallScope.Auto, original).BuildMachineScopeRelaunchArgs(original);

        args.Should().Contain("/LOG=C:\\logs\\setup.log");
        args.Should().Contain("/lang=uk");
        args.Should().Contain("/allusers");
        args.Should().NotContain("/currentuser");
    }

    // ── R117: the elevated instance resumes where the user left off ───────────

    private static readonly string MachineRoot = ScopeLayout.For(InstallScope.Machine).InstallRoot;
    private static readonly string UserRoot = ScopeLayout.For(InstallScope.User).InstallRoot;

    [Fact]
    public void The_relaunch_forwards_a_typed_destination_and_the_resume_token()
    {
        // Arrange
        var typed = Path.Combine(MachineRoot, "Custom", "Scoped");

        // Act
        var args = SessionFor(InstallScope.Auto).BuildMachineScopeRelaunchArgs(Array.Empty<string>(), typed);

        // Assert
        args.Should().Equal("/allusers", "/D=" + typed, InstallSession.ScopeHandoffSwitch);
    }

    [Fact]
    public void An_untouched_machine_default_is_left_for_the_child_to_compute()
    {
        // Arrange — the child alone can see a machine install whose folder must be kept.
        var session = SessionFor(InstallScope.Auto);
        var prefill = session.ResolveDefaultInstallDir(InstallScope.Machine);

        // Act
        var args = session.BuildMachineScopeRelaunchArgs(Array.Empty<string>(), prefill + "\\");

        // Assert
        args.Should().NotContain(a => a.StartsWith("/D=", StringComparison.OrdinalIgnoreCase));
        args.Should().Contain(InstallSession.ScopeHandoffSwitch);
    }

    [Fact]
    public void A_forwarded_destination_replaces_the_parents_own_D()
    {
        // Arrange
        var original = new[] { "/D=" + Path.Combine(MachineRoot, "FromCli") };
        var typed = Path.Combine(MachineRoot, "Typed");

        // Act
        var args = SessionFor(InstallScope.Auto, original).BuildMachineScopeRelaunchArgs(original, typed);

        // Assert
        args.Where(a => a.StartsWith("/D=", StringComparison.OrdinalIgnoreCase))
            .Should().Equal("/D=" + typed);
    }

    [Fact]
    public void An_inbound_resume_token_is_not_doubled()
    {
        // Arrange
        var original = new[] { InstallSession.ScopeHandoffSwitch };

        // Act
        var args = SessionFor(InstallScope.Auto).BuildMachineScopeRelaunchArgs(original);

        // Assert
        args.Should().ContainSingle(a => a == InstallSession.ScopeHandoffSwitch);
    }

    [WindowsFact("DPAPI (crypt32) and Windows file ACLs")]
    public void A_secret_parameter_still_crosses_in_the_envelope_and_not_on_the_line()
    {
        // Arrange
        var blob = BlobWithScope(InstallScope.Auto) with
        {
            Parameters = new[]
            {
                new ParameterDefinition(
                    "apikey", ParameterType.Secret, Default: null, EnumValues: null,
                    InstallTime: false, Description: null, Pattern: null, Min: null, Max: null),
            },
        };
        var original = new[] { "/Papikey=hunter2" };
        var session = InstallSession.ForTesting(blob, CommandLineParser.Parse(original, blob.Parameters));

        // Act
        var args = session.BuildMachineScopeRelaunchArgs(original, Path.Combine(MachineRoot, "Typed"));

        try
        {
            // Assert
            args.Should().NotContain(a => a.Contains("hunter2", StringComparison.Ordinal));
            args.Should().ContainSingle(a => a.StartsWith("/SecretHandoff=", StringComparison.OrdinalIgnoreCase));
            args.Should().Contain(InstallSession.ScopeHandoffSwitch);
        }
        finally
        {
            ElevationSecretHandoff.CleanUp(args, childMayStillBeRunning: false);
        }
    }

    [Fact]
    public void The_child_resumes_only_on_the_shape_the_handoff_produces()
    {
        // Arrange + Act + Assert
        SessionFor(InstallScope.Auto, "/allusers", InstallSession.ScopeHandoffSwitch)
            .ResumesFromScopeHandoff.Should().BeTrue();

        SessionFor(InstallScope.Auto, InstallSession.ScopeHandoffSwitch)
            .ResumesFromScopeHandoff.Should().BeFalse("without /allusers the run is not machine-scoped");
        SessionFor(InstallScope.Auto, "/allusers")
            .ResumesFromScopeHandoff.Should().BeFalse("no token, no resume");
        SessionFor(InstallScope.Auto, "/silent", "/allusers", InstallSession.ScopeHandoffSwitch)
            .ResumesFromScopeHandoff.Should().BeFalse("a silent run has no wizard to resume");
        SessionFor(InstallScope.Auto, "/Uninstall", "/allusers", InstallSession.ScopeHandoffSwitch)
            .ResumesFromScopeHandoff.Should().BeFalse();
    }

    [Fact]
    public void The_child_pre_fills_the_forwarded_destination_in_machine_scope()
    {
        // Arrange
        var typed = Path.Combine(MachineRoot, "Custom", "Scoped");
        var parent = SessionFor(InstallScope.Auto);
        var relaunch = parent.BuildMachineScopeRelaunchArgs(Array.Empty<string>(), typed);

        // Act
        var child = SessionFor(InstallScope.Auto, relaunch.ToArray());

        // Assert
        child.ResolvedScope.Should().Be(InstallScope.Machine);
        child.ResumesFromScopeHandoff.Should().BeTrue();
        child.ScopeHandoffDestinationAccepted.Should().BeTrue();
        child.ResolveDefaultInstallDir().Should().Be(typed);
    }

    [Fact]
    public void Containment_still_refuses_a_user_scope_path_in_machine_scope()
    {
        // Arrange
        var userPath = Path.Combine(UserRoot, "Scoped");
        var parent = SessionFor(InstallScope.Auto);

        // Act
        var parentAccepts = parent.AcceptsInstallDir(InstallScope.Machine, userPath);
        var child = SessionFor(InstallScope.Auto, "/allusers", "/D=" + userPath, InstallSession.ScopeHandoffSwitch);

        // Assert
        parentAccepts.Should().BeFalse("the wizard refuses it before asking for administrator approval");
        parent.AcceptsInstallDir(InstallScope.Machine, Path.Combine(MachineRoot, "Scoped")).Should().BeTrue();
        child.ScopeHandoffDestinationAccepted.Should().BeFalse(
            "a forged or stale /D= gets no further in the elevated instance");
        child.ResolveDefaultInstallDir().Should().NotBe(userPath);
    }

    [Fact]
    public void The_resume_token_parses_and_renders()
    {
        // Act
        var parsed = CommandLineParser.Parse(new[] { "/scopehandoff" }, Array.Empty<ParameterDefinition>());

        // Assert
        parsed.ScopeHandoff.Should().BeTrue();
        parsed.AuditSafeRendering().Should().Be(InstallSession.ScopeHandoffSwitch);
        CommandLineParser.Parse(Array.Empty<string>(), Array.Empty<ParameterDefinition>())
            .ScopeHandoff.Should().BeFalse();
    }
}
