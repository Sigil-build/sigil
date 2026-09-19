// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Steps;
using SigilBuild.Wrapper.Tests.Helpers;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Steps;

/// <summary>
/// <c>file_copy</c> records the directories it creates, so rollback and uninstall
/// can remove them.
/// </summary>
/// <remarks>
/// The step journalled its files and nothing else, while creating the whole
/// destination tree on the way. Uninstalling therefore removed every file and left
/// the directory skeleton behind — empty, and permanently, because nothing had ever
/// written down that the install made it. Found by uninstalling a real packaged
/// application and looking at what remained.
/// </remarks>
public class FileCopyDirectoryJournalTests
{
    private static async Task<StepResult> CopyTreeAsync(
        string sourceRoot, string destination, RollbackJournal journal)
    {
        var spec = new InstallStep.FileCopy(
            "deploy",
            Path.Combine(sourceRoot, "**"),
            destination,
            Overwrite: true,
            When: null,
            OnFailure: OnFailure.Rollback);
        return await new FileCopyStep(spec).RunAsync(StepContext.Empty, journal, default);
    }

    private static void Write(string root, string relative, string content)
    {
        var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    [Fact]
    public async Task Rollback_removes_the_directory_tree_the_copy_created()
    {
        // Arrange — a payload with nesting, and a destination that does not exist yet.
        using var src = new TempDir();
        using var work = new TempDir();
        Write(src.Path, "readme.txt", "top");
        Write(src.Path, "docs/guide.txt", "nested");
        Write(src.Path, "docs/img/logo.txt", "deeper");

        var destination = Path.Combine(work.Path, "install", "App");
        var journal = new RollbackJournal();

        // Act
        var result = await CopyTreeAsync(src.Path, destination, journal);
        result.Success.Should().BeTrue();
        Directory.Exists(Path.Combine(destination, "docs", "img")).Should().BeTrue("precondition");

        await journal.UndoAsync(ReplayAnchorage.InProcess);

        // Assert — nothing the step created survives.
        Directory.Exists(destination).Should().BeFalse(
            "the install directory was created by this step, so rolling back must remove it");
        Directory.Exists(Path.Combine(work.Path, "install")).Should().BeFalse(
            "CreateDirectory silently creates parents too — those are this step's doing as well");
        Directory.Exists(work.Path).Should().BeTrue(
            "the directory that already existed is not ours to remove");
    }

    [Fact]
    public async Task A_destination_that_already_existed_is_left_alone_by_rollback()
    {
        // Arrange — the destination is pre-existing, as it is for an upgrade into a
        // directory the user already has.
        using var src = new TempDir();
        using var dst = new TempDir();
        Write(src.Path, "app.txt", "bytes");

        var journal = new RollbackJournal();

        // Act
        (await CopyTreeAsync(src.Path, dst.Path, journal)).Success.Should().BeTrue();
        await journal.UndoAsync(ReplayAnchorage.InProcess);

        // Assert
        Directory.Exists(dst.Path).Should().BeTrue(
            "removing a directory the run did not create is worse than leaving one behind");
        File.Exists(Path.Combine(dst.Path, "app.txt")).Should().BeFalse(
            "the file it did create is still rolled back");
    }

    [Fact]
    public async Task A_directory_the_user_filled_afterwards_survives_rollback()
    {
        // Arrange
        using var src = new TempDir();
        using var work = new TempDir();
        Write(src.Path, "data/app.txt", "bytes");

        var destination = Path.Combine(work.Path, "App");
        var journal = new RollbackJournal();
        (await CopyTreeAsync(src.Path, destination, journal)).Success.Should().BeTrue();

        // The application wrote something of its own into the directory we made.
        var userFile = Path.Combine(destination, "data", "user.db");
        File.WriteAllText(userFile, "user data");

        // Act
        await journal.UndoAsync(ReplayAnchorage.InProcess);

        // Assert
        File.Exists(userFile).Should().BeTrue("rollback undoes this run, not the user's work");
        Directory.Exists(Path.Combine(destination, "data")).Should().BeTrue(
            "the removal is conditional on the directory being empty, and it is not");
    }
}
