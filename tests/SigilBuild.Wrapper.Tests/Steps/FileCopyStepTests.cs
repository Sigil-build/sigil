using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Steps;
using SigilBuild.Wrapper.Tests.Helpers;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Steps;

public class FileCopyStepTests
{
    [Fact]
    public async Task Copies_a_single_file_to_the_destination()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        var srcFile = Path.Combine(src.Path, "hello.txt");
        File.WriteAllText(srcFile, "hi");

        var spec = new InstallStep.FileCopy("s", srcFile, dst.Path, Overwrite: false, When: null, OnFailure: OnFailure.Fail);
        var step = new FileCopyStep(spec);
        var journal = new RollbackJournal();

        var result = await step.RunAsync(StepContext.Empty, journal, default);

        result.Success.Should().BeTrue();
        File.Exists(Path.Combine(dst.Path, "hello.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task Copies_a_glob_recursively()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        File.WriteAllText(Path.Combine(src.Path, "a.txt"), "a");
        var sub = Path.Combine(src.Path, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "b.txt"), "b");

        var spec = new InstallStep.FileCopy("s", src.Path + "/**", dst.Path, Overwrite: true, When: null, OnFailure: OnFailure.Fail);
        var step = new FileCopyStep(spec);
        var journal = new RollbackJournal();

        var result = await step.RunAsync(StepContext.Empty, journal, default);

        result.Success.Should().BeTrue();
        File.Exists(Path.Combine(dst.Path, "a.txt")).Should().BeTrue();
        File.Exists(Path.Combine(dst.Path, "sub", "b.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task Rollback_removes_copied_files()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        File.WriteAllText(Path.Combine(src.Path, "a.txt"), "a");
        File.WriteAllText(Path.Combine(src.Path, "b.txt"), "b");

        var spec = new InstallStep.FileCopy("s", src.Path + "/**", dst.Path, Overwrite: true, When: null, OnFailure: OnFailure.Rollback);
        var step = new FileCopyStep(spec);
        var journal = new RollbackJournal();

        await step.RunAsync(StepContext.Empty, journal, default);
        Directory.GetFiles(dst.Path).Should().HaveCount(2);

        await journal.UndoAsync(default);

        Directory.GetFiles(dst.Path).Should().BeEmpty();
    }

    [Fact]
    public async Task Rollback_restores_backup_of_pre_existing_files()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        File.WriteAllText(Path.Combine(src.Path, "a.txt"), "new");
        var preExisting = Path.Combine(dst.Path, "a.txt");
        File.WriteAllText(preExisting, "old");

        var spec = new InstallStep.FileCopy("s", src.Path + "/**", dst.Path, Overwrite: true, When: null, OnFailure: OnFailure.Rollback);
        var step = new FileCopyStep(spec);
        var journal = new RollbackJournal();

        await step.RunAsync(StepContext.Empty, journal, default);
        File.ReadAllText(preExisting).Should().Be("new");

        await journal.UndoAsync(default);

        File.Exists(preExisting).Should().BeTrue("pre-existing file must be restored");
        File.ReadAllText(preExisting).Should().Be("old");
        File.Exists(preExisting + ".sigil-bak").Should().BeFalse("backup must be cleaned up");
    }

    [Fact]
    public async Task Rolling_back_twice_is_safe()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        File.WriteAllText(Path.Combine(src.Path, "a.txt"), "a");

        var spec = new InstallStep.FileCopy("s", src.Path + "/**", dst.Path, Overwrite: true, When: null, OnFailure: OnFailure.Rollback);
        var step = new FileCopyStep(spec);
        var journal = new RollbackJournal();

        await step.RunAsync(StepContext.Empty, journal, default);
        await journal.UndoAsync(default);
        // Second undo should not throw — the journal swallows individual failures.
        await journal.UndoAsync(default);

        Directory.GetFiles(dst.Path).Should().BeEmpty();
    }
}
