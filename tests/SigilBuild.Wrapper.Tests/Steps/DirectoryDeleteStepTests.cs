using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Steps;
using SigilBuild.Wrapper.Tests.Helpers;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Steps;

public class DirectoryDeleteStepTests
{
    [Fact]
    public async Task Deletes_an_empty_directory()
    {
        using var parent = new TempDir();
        var target = Path.Combine(parent.Path, "todelete");
        Directory.CreateDirectory(target);

        var spec = new InstallStep.DirectoryDelete("dd", target, Recursive: false, When: null, OnFailure: OnFailure.Fail);
        var step = new DirectoryDeleteStep(spec);
        var journal = new RollbackJournal();

        var result = await step.RunAsync(StepContext.Empty, journal, default);

        result.Success.Should().BeTrue();
        Directory.Exists(target).Should().BeFalse();

        // Success path: no rollback runs, so mirror the install commit and reclaim
        // the transient rollback stash so this test leaves no %TEMP%\sigil-dd-*
        // residue (regression guard for T17 temp-dir cleanliness).
        journal.DiscardTransientStashes();
        AssertNoStashResidue(journal);
    }

    /// <summary>
    /// Asserts every stash-bearing rollback record's <c>%TEMP%</c> stash was
    /// reclaimed — the property the install-commit path (and these tests) must
    /// uphold so a full <c>dotnet test</c> run leaves no <c>sigil-dd-*</c> residue.
    /// </summary>
    private static void AssertNoStashResidue(RollbackJournal journal)
    {
        foreach (var record in journal.Records)
        {
            if (record is RollbackRecord.RestoreDeletedDirectory d)
            {
                Directory.Exists(d.StashPath).Should().BeFalse(
                    "the transient directory_delete stash must be reclaimed on commit");
            }
        }
    }

    [Fact]
    public async Task Rollback_restores_deleted_directory_with_contents()
    {
        using var parent = new TempDir();
        var target = Path.Combine(parent.Path, "todelete");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "a.txt"), "hello");
        var sub = Path.Combine(target, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "b.txt"), "world");

        var spec = new InstallStep.DirectoryDelete("dd", target, Recursive: true, When: null, OnFailure: OnFailure.Rollback);
        var step = new DirectoryDeleteStep(spec);
        var journal = new RollbackJournal();

        var result = await step.RunAsync(StepContext.Empty, journal, default);
        result.Success.Should().BeTrue();
        Directory.Exists(target).Should().BeFalse("directory should be gone after step");

        await journal.UndoAsync(default);

        Directory.Exists(target).Should().BeTrue("rollback must restore the directory");
        File.Exists(Path.Combine(target, "a.txt")).Should().BeTrue();
        File.ReadAllText(Path.Combine(target, "a.txt")).Should().Be("hello");
        File.Exists(Path.Combine(target, "sub", "b.txt")).Should().BeTrue();
        File.ReadAllText(Path.Combine(target, "sub", "b.txt")).Should().Be("world");
    }

    [Fact]
    public async Task Non_recursive_on_non_empty_directory_returns_failure()
    {
        using var parent = new TempDir();
        var target = Path.Combine(parent.Path, "notempty");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "file.txt"), "data");

        var spec = new InstallStep.DirectoryDelete("dd", target, Recursive: false, When: null, OnFailure: OnFailure.Fail);
        var step = new DirectoryDeleteStep(spec);
        var journal = new RollbackJournal();

        var result = await step.RunAsync(StepContext.Empty, journal, default);

        result.Success.Should().BeFalse("non-recursive delete must refuse non-empty directories");
        Directory.Exists(target).Should().BeTrue("the directory must not be partially deleted");
        journal.Records.Should().BeEmpty("nothing was deleted so no rollback needed");
    }

    [Fact]
    public async Task Missing_directory_succeeds_without_recording_rollback()
    {
        using var parent = new TempDir();
        var target = Path.Combine(parent.Path, "nonexistent");

        var spec = new InstallStep.DirectoryDelete("dd", target, Recursive: false, When: null, OnFailure: OnFailure.Fail);
        var step = new DirectoryDeleteStep(spec);
        var journal = new RollbackJournal();

        var result = await step.RunAsync(StepContext.Empty, journal, default);

        result.Success.Should().BeTrue("a missing directory is treated as already deleted");
        journal.Records.Should().BeEmpty("nothing to roll back");
    }

    [Fact]
    public async Task StepFactory_creates_DirectoryDeleteStep_without_throwing()
    {
        var spec = new InstallStep.DirectoryDelete(
            "dd",
            Path: "/tmp/nonexistent",
            Recursive: true,
            When: null,
            OnFailure: OnFailure.Fail);

        var step = StepFactory.Create(spec);

        step.Should().NotBeNull();
        await Task.CompletedTask;
    }
}
