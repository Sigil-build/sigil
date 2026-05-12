using System.IO;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Steps;
using SigilBuild.Wrapper.Tests.Helpers;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Steps;

public class FileDeleteStepTests
{
    [Fact]
    public async Task Deletes_existing_file()
    {
        using var dir = new TempDir();
        var target = Path.Combine(dir.Path, "victim.txt");
        File.WriteAllText(target, "data");

        var spec = new InstallStep.FileDelete("fd", target, IfMissing: "fail", When: null, OnFailure: OnFailure.Fail);
        var step = new FileDeleteStep(spec);
        var journal = new RollbackJournal();

        var result = await step.RunAsync(StepContext.Empty, journal, default);

        result.Success.Should().BeTrue();
        File.Exists(target).Should().BeFalse();
    }

    [Fact]
    public async Task Rollback_restores_deleted_file_byte_identical()
    {
        using var dir = new TempDir();
        var target = Path.Combine(dir.Path, "victim.txt");
        var originalBytes = Encoding.UTF8.GetBytes("original content byte-identical");
        File.WriteAllBytes(target, originalBytes);

        var spec = new InstallStep.FileDelete("fd", target, IfMissing: "fail", When: null, OnFailure: OnFailure.Rollback);
        var step = new FileDeleteStep(spec);
        var journal = new RollbackJournal();

        var result = await step.RunAsync(StepContext.Empty, journal, default);
        result.Success.Should().BeTrue();
        File.Exists(target).Should().BeFalse("file should be gone after step");

        await journal.UndoAsync(default);

        File.Exists(target).Should().BeTrue("rollback must restore the file");
        File.ReadAllBytes(target).Should().Equal(originalBytes, "restored bytes must be identical");
    }

    [Fact]
    public async Task Missing_file_with_if_missing_skip_succeeds_without_recording_rollback()
    {
        using var dir = new TempDir();
        var target = Path.Combine(dir.Path, "nonexistent.txt");

        var spec = new InstallStep.FileDelete("fd", target, IfMissing: "skip", When: null, OnFailure: OnFailure.Fail);
        var step = new FileDeleteStep(spec);
        var journal = new RollbackJournal();

        var result = await step.RunAsync(StepContext.Empty, journal, default);

        result.Success.Should().BeTrue("skip means a missing target is acceptable");
        journal.Records.Should().BeEmpty("no rollback needed when nothing was deleted");
    }

    [Fact]
    public async Task Missing_file_with_if_missing_fail_returns_failure()
    {
        using var dir = new TempDir();
        var target = Path.Combine(dir.Path, "nonexistent.txt");

        var spec = new InstallStep.FileDelete("fd", target, IfMissing: "fail", When: null, OnFailure: OnFailure.Fail);
        var step = new FileDeleteStep(spec);
        var journal = new RollbackJournal();

        var result = await step.RunAsync(StepContext.Empty, journal, default);

        result.Success.Should().BeFalse("fail mode must propagate the missing-file error");
        journal.Records.Should().BeEmpty("nothing to roll back when step did not act");
    }

    [Fact]
    public async Task StepFactory_creates_FileDeleteStep_without_throwing()
    {
        var spec = new InstallStep.FileDelete(
            "fd",
            Path: "/tmp/nonexistent",
            IfMissing: "skip",
            When: null,
            OnFailure: OnFailure.Fail);

        var step = StepFactory.Create(spec);

        step.Should().NotBeNull();
        await Task.CompletedTask;
    }
}
