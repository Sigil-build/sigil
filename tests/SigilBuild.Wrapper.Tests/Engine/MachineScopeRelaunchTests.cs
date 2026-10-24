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

        args.Should().ContainSingle().Which.Should().Be("/allusers");
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

        args.Should().ContainSingle().Which.Should().Be("/allusers");
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
}
