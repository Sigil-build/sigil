namespace SigilBuild.Wrapper.Tests.Engine;

using System;
using System.Collections.Generic;
using FluentAssertions;
using SigilBuild.Wrapper.Engine;
using Xunit;

/// <summary>
/// Register row R29 — the de-elevation fallback.
/// </summary>
/// <remarks>
/// <para>
/// The primary path is correct: an elevated installer launches the app under the desktop
/// shell's medium-integrity primary token via <c>CreateProcessWithTokenW</c>. On any
/// failure of that path the code used to fall through to a plain <c>Process.Start</c>,
/// which hands the launched application the INSTALLER'S ADMIN TOKEN — with no log line
/// and no user-visible signal. One de-elevation failure (no interactive shell, a token
/// that will not duplicate) silently undid the entire mechanism, and P2's own acceptance
/// criterion with it.
/// </para>
/// <para>
/// The elevated branch cannot be reached on an unelevated test runner, and forcing a
/// real de-elevation failure on an elevated one would mean breaking the desktop shell.
/// The three effects are therefore injected: the assertion is on the DECISION — was the
/// direct spawn reached at all — which is the thing that was wrong.
/// </para>
/// </remarks>
public class LaunchDeElevationTests
{
    [Fact]
    public void An_elevated_installer_that_cannot_de_elevate_does_not_launch_at_all()
    {
        var directSpawnCalls = 0;

        var outcome = Launcher.LaunchCore(
            @"C:\Program Files\Acme\Acme.exe",
            args: null,
            isElevated: () => true,
            deElevatedLaunch: (_, _) => false,   // no shell / token would not duplicate
            directLaunch: (_, _) => { directSpawnCalls++; return true; });

        directSpawnCalls.Should().Be(
            0,
            "a plain spawn from an elevated installer gives the application the installer's " +
            "administrator token for the rest of its lifetime — losing a convenience launch " +
            "is the smaller harm, and it is the one P2 promised");
        outcome.Should().Be(
            LaunchOutcome.SkippedDeElevationUnavailable,
            "the caller has to be able to tell a refusal from a spawn failure in order to log it");
    }

    [Fact]
    public void An_elevated_installer_that_can_de_elevate_launches_through_the_shell_token()
    {
        var directSpawnCalls = 0;

        var outcome = Launcher.LaunchCore(
            @"C:\Program Files\Acme\Acme.exe",
            args: null,
            isElevated: () => true,
            deElevatedLaunch: (_, _) => true,
            directLaunch: (_, _) => { directSpawnCalls++; return true; });

        outcome.Should().Be(LaunchOutcome.Started);
        directSpawnCalls.Should().Be(0, "the de-elevated path already started it");
    }

    /// <summary>
    /// The over-refusal guard. A per-user install runs unelevated, where a plain spawn
    /// already runs at the user's own integrity level — R29 must not cost that its launch.
    /// </summary>
    [Fact]
    public void An_unelevated_installer_still_launches_directly()
    {
        var directSpawnCalls = 0;

        var outcome = Launcher.LaunchCore(
            @"C:\Users\jo\AppData\Local\Acme\Acme.exe",
            args: new List<string> { "--first-run" },
            isElevated: () => false,
            deElevatedLaunch: (_, _) => throw new InvalidOperationException(
                "an unelevated installer has nothing to de-elevate from"),
            directLaunch: (_, _) => { directSpawnCalls++; return true; });

        outcome.Should().Be(LaunchOutcome.Started);
        directSpawnCalls.Should().Be(1);
    }

    [Fact]
    public void A_blank_target_is_not_a_launch_failure()
    {
        Launcher.LaunchCore(
            "   ",
            args: null,
            isElevated: () => true,
            deElevatedLaunch: (_, _) => true,
            directLaunch: (_, _) => true)
            .Should().Be(LaunchOutcome.NothingToLaunch);
    }
}
