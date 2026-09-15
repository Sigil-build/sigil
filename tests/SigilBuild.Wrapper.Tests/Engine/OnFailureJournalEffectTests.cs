// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Tests.Helpers;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Engine;

/// <summary>
/// What the rollback journal actually did, per <c>on_failure</c> value (R78).
/// </summary>
/// <remarks>
/// This is the assertion the suite was missing, and its absence is why
/// <c>fail</c> and <c>rollback</c> could share one arm of the engine's switch
/// unnoticed for the whole of the feature-parity track: every existing test
/// asserted <c>continue</c> against "everything else", never one abort mode
/// against the other. Each test here drives a real first step that leaves a
/// visible artefact on disk, fails the second, and then asserts on the artefact —
/// so a future collapse of the two modes fails here rather than in a user's
/// install.
/// </remarks>
public class OnFailureJournalEffectTests
{
    /// <summary>A step type the StepFactory throws on, to fail step 2 for real.</summary>
    private static InstallStep.RegistryWrite FailingStep(OnFailure onFailure) =>
        new InstallStep.RegistryWrite("boom", "HKLM", "K", "N", "REG_SZ", "V", "native", null, onFailure);

    [Fact]
    public async Task Rollback_unwinds_the_work_the_earlier_step_did()
    {
        // Arrange
        using var src = new TempDir();
        using var dst = new TempDir();
        File.WriteAllText(Path.Combine(src.Path, "a.txt"), "1");

        var steps = new InstallStep[]
        {
            new InstallStep.FileCopy("s1",
                From: Path.Combine(src.Path, "*.txt"),
                To: dst.Path,
                Overwrite: true,
                When: null,
                OnFailure: OnFailure.Rollback),
            FailingStep(OnFailure.Rollback),
        };

        // Act
        var result = await new InstallEngine().RunAsync(steps, StepContext.Empty);

        // Assert
        result.Success.Should().BeFalse();
        Directory.GetFiles(dst.Path).Should().BeEmpty(
            "rollback replays the journal in reverse, so s1's copy must be gone");
    }

    [Fact]
    public async Task Continue_leaves_the_earlier_work_in_place_and_the_run_succeeds()
    {
        // Arrange
        using var src = new TempDir();
        using var dst = new TempDir();
        File.WriteAllText(Path.Combine(src.Path, "a.txt"), "1");

        var steps = new InstallStep[]
        {
            new InstallStep.FileCopy("s1",
                From: Path.Combine(src.Path, "*.txt"),
                To: dst.Path,
                Overwrite: true,
                When: null,
                OnFailure: OnFailure.Rollback),
            FailingStep(OnFailure.Continue),
        };

        // Act
        var result = await new InstallEngine().RunAsync(steps, StepContext.Empty);

        // Assert
        result.Success.Should().BeTrue("a continued failure is not a failed install");
        Directory.GetFiles(dst.Path).Should().ContainSingle(
            "nothing was unwound, so s1's copy survives");
    }

    [Fact]
    public async Task An_unrecognised_mode_still_unwinds_rather_than_proceeding()
    {
        // Arrange — the engine's default arm is deliberately fail-closed. Reaching
        // it from a manifest is no longer possible (the parser refuses anything but
        // rollback/continue for a journalled phase), but the object model can still
        // express it, and "unknown means keep going" would be the dangerous reading.
        using var src = new TempDir();
        using var dst = new TempDir();
        File.WriteAllText(Path.Combine(src.Path, "a.txt"), "1");

        var steps = new InstallStep[]
        {
            new InstallStep.FileCopy("s1",
                From: Path.Combine(src.Path, "*.txt"),
                To: dst.Path,
                Overwrite: true,
                When: null,
                OnFailure: OnFailure.Rollback),
            FailingStep((OnFailure)999),
        };

        // Act
        var result = await new InstallEngine().RunAsync(steps, StepContext.Empty);

        // Assert
        result.Success.Should().BeFalse();
        Directory.GetFiles(dst.Path).Should().BeEmpty();
    }
}
