// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Steps;
using SigilBuild.Wrapper.Tests.Helpers;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Steps;

/// <summary>
/// <c>file_copy</c>'s <c>overwrite:</c> flag, asserted on the destination's
/// <em>content</em> (R77).
/// </summary>
/// <remarks>
/// Content is the assertion that matters and the one nothing made before: the step
/// reported success either way, so every existing test passed while
/// <c>overwrite: false</c> destroyed the file it promised to preserve. This is the
/// flag a publisher reaches for to protect a user's settings across an upgrade, so
/// "did the bytes survive" is the only question worth asking of it.
/// </remarks>
public class FileCopyOverwriteTests
{
    private static (string Source, string Destination) TwoFiles(
        TempDir src, TempDir dst, string sourceText, string destinationText)
    {
        var source = Path.Combine(src.Path, "config.json");
        var destination = Path.Combine(dst.Path, "config.json");
        File.WriteAllText(source, sourceText);
        File.WriteAllText(destination, destinationText);
        return (source, destination);
    }

    private static async Task<StepResult> RunCopyAsync(
        string from, string to, bool overwrite, StepContext ctx, RollbackJournal journal)
    {
        var spec = new InstallStep.FileCopy(
            "copy_config", from, to, Overwrite: overwrite, When: null, OnFailure: OnFailure.Rollback);
        return await new FileCopyStep(spec).RunAsync(ctx, journal, default);
    }

    [Fact]
    public async Task Overwrite_false_leaves_an_existing_destination_untouched()
    {
        // Arrange — the user's settings file is already there and must survive.
        using var src = new TempDir();
        using var dst = new TempDir();
        var (from, destination) = TwoFiles(src, dst, sourceText: "SHIPPED", destinationText: "USER EDITED");

        // Act
        var result = await RunCopyAsync(from, dst.Path, overwrite: false, StepContext.Empty, new RollbackJournal());

        // Assert
        result.Success.Should().BeTrue(
            "the destination is already in the state the manifest asked for, so this is not a failure");
        File.ReadAllText(destination).Should().Be("USER EDITED",
            "overwrite: false promises the existing file is left alone");
    }

    [Fact]
    public async Task Overwrite_true_replaces_an_existing_destination()
    {
        // Arrange
        using var src = new TempDir();
        using var dst = new TempDir();
        var (from, destination) = TwoFiles(src, dst, sourceText: "SHIPPED", destinationText: "USER EDITED");

        // Act
        var result = await RunCopyAsync(from, dst.Path, overwrite: true, StepContext.Empty, new RollbackJournal());

        // Assert
        result.Success.Should().BeTrue();
        File.ReadAllText(destination).Should().Be("SHIPPED");
    }

    [Fact]
    public async Task Overwrite_false_still_copies_when_the_destination_is_absent()
    {
        // Arrange — the flag guards existing files; it is not "never copy".
        using var src = new TempDir();
        using var dst = new TempDir();
        var from = Path.Combine(src.Path, "config.json");
        File.WriteAllText(from, "SHIPPED");

        // Act
        var result = await RunCopyAsync(from, dst.Path, overwrite: false, StepContext.Empty, new RollbackJournal());

        // Assert
        result.Success.Should().BeTrue();
        File.ReadAllText(Path.Combine(dst.Path, "config.json")).Should().Be("SHIPPED");
    }

    [Fact]
    public async Task A_preserved_file_is_not_journalled_and_leaves_no_backup_behind()
    {
        // Arrange
        using var src = new TempDir();
        using var dst = new TempDir();
        var (from, destination) = TwoFiles(src, dst, sourceText: "SHIPPED", destinationText: "USER EDITED");
        var journal = new RollbackJournal();

        // Act
        await RunCopyAsync(from, dst.Path, overwrite: false, StepContext.Empty, journal);

        // Assert — nothing was written, so there is nothing to undo. Journalling a
        // restore of bytes we never touched would cost a full backup copy and leave
        // a .sigil-bak file behind for no gain.
        journal.Records.Should().BeEmpty();
        File.Exists(destination + ".sigil-bak").Should().BeFalse();

        await journal.UndoAsync(ReplayAnchorage.InProcess);
        File.ReadAllText(destination).Should().Be("USER EDITED",
            "a rollback must not disturb a file the install never wrote");
    }

    [Fact]
    public async Task A_preserved_file_says_so_in_the_log()
    {
        // Arrange — a silent no-op is how this class of defect hides, so the skip
        // is reported rather than merely performed.
        using var src = new TempDir();
        using var dst = new TempDir();
        var (from, _) = TwoFiles(src, dst, sourceText: "SHIPPED", destinationText: "USER EDITED");

        var sink = new CollectingProgress();
        var ctx = StepContext.Empty;
        ctx.ProgressSink = sink;

        // Act
        await RunCopyAsync(from, dst.Path, overwrite: false, ctx, new RollbackJournal());

        // Assert
        sink.Messages.Should().ContainSingle(m =>
            m.Contains("config.json", StringComparison.Ordinal) &&
            m.Contains("kept", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class CollectingProgress : IProgress<StepProgress>
    {
        private readonly List<StepProgress> _reports = new();

        public IEnumerable<string> Messages => _reports.Select(r => r.Message ?? string.Empty);

        public void Report(StepProgress value) => _reports.Add(value);
    }
}
