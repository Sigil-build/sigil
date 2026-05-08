using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Steps;
using SigilBuild.Wrapper.Tests.Helpers;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Steps;

public class DirectoryCreateStepTests
{
    [Fact]
    public async Task Creates_a_new_directory()
    {
        using var temp = new TempDir();
        var target = Path.Combine(temp.Path, "new-dir");

        var spec = new InstallStep.DirectoryCreate("s", target, When: null, OnFailure: OnFailure.Fail);
        var step = new DirectoryCreateStep(spec);
        var journal = new RollbackJournal();

        var result = await step.RunAsync(StepContext.Empty, journal, default);

        result.Success.Should().BeTrue();
        Directory.Exists(target).Should().BeTrue();
        journal.Records.Should().HaveCount(1);
    }

    [Fact]
    public async Task No_ops_on_existing_directory_with_no_rollback_record()
    {
        using var temp = new TempDir();
        // temp.Path itself exists already.

        var spec = new InstallStep.DirectoryCreate("s", temp.Path, When: null, OnFailure: OnFailure.Fail);
        var step = new DirectoryCreateStep(spec);
        var journal = new RollbackJournal();

        var result = await step.RunAsync(StepContext.Empty, journal, default);

        result.Success.Should().BeTrue();
        journal.Records.Should().BeEmpty("must not record rollback for a pre-existing directory");
    }

    [Fact]
    public async Task Rollback_removes_only_previously_absent_empty_dirs()
    {
        using var temp = new TempDir();
        var target = Path.Combine(temp.Path, "fresh");

        var spec = new InstallStep.DirectoryCreate("s", target, When: null, OnFailure: OnFailure.Rollback);
        var step = new DirectoryCreateStep(spec);
        var journal = new RollbackJournal();

        await step.RunAsync(StepContext.Empty, journal, default);
        Directory.Exists(target).Should().BeTrue();

        await journal.UndoAsync(default);

        Directory.Exists(target).Should().BeFalse("empty newly-created dir must be removed");
    }

    [Fact]
    public async Task Rollback_keeps_dir_if_files_were_added_after_create()
    {
        using var temp = new TempDir();
        var target = Path.Combine(temp.Path, "fresh");

        var spec = new InstallStep.DirectoryCreate("s", target, When: null, OnFailure: OnFailure.Rollback);
        var step = new DirectoryCreateStep(spec);
        var journal = new RollbackJournal();

        await step.RunAsync(StepContext.Empty, journal, default);
        File.WriteAllText(Path.Combine(target, "stranger.txt"), "x");

        await journal.UndoAsync(default);

        Directory.Exists(target).Should().BeTrue("non-empty dir must not be removed by rollback");
    }
}
