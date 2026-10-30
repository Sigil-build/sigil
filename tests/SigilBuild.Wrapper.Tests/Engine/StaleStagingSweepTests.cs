// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using FluentAssertions;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Tests.Helpers;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Engine;

/// <summary>
/// The sweep that removes the <c>sigil-uninstall-&lt;guid&gt;</c> directories earlier
/// per-user uninstalls left behind. Every test sweeps its own scratch root and never the
/// real <c>%TEMP%</c>. (R114)
/// </summary>
public sealed class StaleStagingSweepTests : IDisposable
{
    private static readonly TimeSpan Age = StaleStagingSweep.MinimumAge;

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "sigil-r114-tests-" + Guid.NewGuid().ToString("N"));

    private readonly List<string> _log = new();

    public StaleStagingSweepTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
#pragma warning disable CA1031 // Best-effort test cleanup.
        try
        {
            // Junctions first, so the recursive delete below never walks into a target.
            foreach (var dir in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories))
            {
                if (new DirectoryInfo(dir).LinkTarget is not null)
                {
                    Directory.Delete(dir);
                }
            }
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
#pragma warning restore CA1031
    }

    private static string NewName() => StaleStagingSweep.Prefix + Guid.NewGuid().ToString("N");

    private string Candidate(string? name = null, params string[] files)
    {
        var dir = Path.Combine(_root, name ?? NewName());
        Directory.CreateDirectory(dir);
        foreach (var file in files)
        {
            File.WriteAllText(Path.Combine(dir, file), "x");
        }
        return dir;
    }

    // Swept as if the directories were created well over the threshold ago.
    private int SweepLater(bool requireAdminOnly = false, string? ownDirectory = null) =>
        StaleStagingSweep.Sweep(
            _root, requireAdminOnly, DateTime.UtcNow + TimeSpan.FromMinutes(10), Age, ownDirectory, Log);

    private void Log(string message, bool isError) => _log.Add(message);

    [WindowsFact]
    public void A_stale_uninstaller_copy_directory_is_removed()
    {
        // Arrange
        var stale = Candidate(null, "uninstall.exe", "original-uninstall.exe");

        // Act
        var removed = SweepLater();

        // Assert
        removed.Should().Be(1);
        Directory.Exists(stale).Should().BeFalse();
        _log.Should().Contain(l => l.Contains("removed", StringComparison.Ordinal) && l.Contains(stale, StringComparison.Ordinal));
    }

    [WindowsFact]
    public void An_empty_stale_directory_is_removed()
    {
        // Arrange
        var stale = Candidate();

        // Act
        var removed = SweepLater();

        // Assert
        removed.Should().Be(1);
        Directory.Exists(stale).Should().BeFalse();
    }

    [Theory]
    [InlineData("sigil-uninstall-0123456789abcdef0123456789abcdef", true)]
    [InlineData("sigil-uninstall-0123456789ABCDEF0123456789ABCDEF", false)]
    [InlineData("sigil-uninstall-0123456789abcdef0123456789abcde", false)]
    [InlineData("sigil-uninstall-0123456789abcdef0123456789abcdef0", false)]
    [InlineData("sigil-uninstall-0123456789abcdef0123456789abcdeg", false)]
    [InlineData("sigil-update-0123456789abcdef0123456789abcdef", false)]
    [InlineData("sigil-stage-0123456789abcdef0123456789abcdef", false)]
    [InlineData("sigil-runtime-0123456789abcdef0123456789abcdef", false)]
    [InlineData("sigil-runtime", false)]
    [InlineData("Sigil-uninstall-0123456789abcdef0123456789abcdef", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_the_exact_uninstaller_copy_name_matches(string? name, bool expected)
    {
        // Act
        var matches = StaleStagingSweep.IsSweepableName(name);

        // Assert
        matches.Should().Be(expected);
    }

    [WindowsFact]
    public void A_directory_with_any_other_name_is_kept()
    {
        // Arrange
        var hex = Guid.NewGuid().ToString("N");
        var kept = new[]
        {
            Candidate("sigil-update-" + hex, "Setup.exe"),
            Candidate("sigil-uninstall-" + hex.ToUpperInvariant(), "uninstall.exe"),
            Candidate("sigil-uninstall-" + hex + "-old", "uninstall.exe"),
            Candidate("sigil-uninstall-backup", "uninstall.exe"),
        };

        // Act
        var removed = SweepLater();

        // Assert
        removed.Should().Be(0);
        foreach (var dir in kept)
        {
            Directory.Exists(dir).Should().BeTrue(dir);
        }
    }

    [WindowsFact]
    public void A_matching_name_that_is_a_junction_is_not_followed()
    {
        // Arrange — the matching name is a junction to a directory full of files.
        var target = Candidate("victim", "precious.txt");
        var link = Path.Combine(_root, NewName());
        CreateJunction(link, target);

        // Act
        var removed = SweepLater();

        // Assert
        removed.Should().Be(0);
        File.Exists(Path.Combine(target, "precious.txt")).Should().BeTrue("the sweep never follows a reparse point");
        Directory.Exists(link).Should().BeTrue("the link itself is left alone too");
        _log.Should().Contain(l => l.Contains("reparse point", StringComparison.Ordinal));
    }

    [WindowsFact]
    public void A_directory_holding_a_junction_is_left_alone()
    {
        // Arrange
        var target = Candidate("victim", "precious.txt");
        var stale = Candidate(null, "uninstall.exe");
        CreateJunction(Path.Combine(stale, "inner"), target);

        // Act
        var removed = SweepLater();

        // Assert
        removed.Should().Be(0);
        File.Exists(Path.Combine(stale, "uninstall.exe")).Should().BeTrue("nothing in it is deleted");
        File.Exists(Path.Combine(target, "precious.txt")).Should().BeTrue();
    }

    [WindowsFact]
    public void A_directory_holding_a_subdirectory_is_left_alone()
    {
        // Arrange
        var stale = Candidate(null, "uninstall.exe");
        Directory.CreateDirectory(Path.Combine(stale, "sub"));

        // Act
        var removed = SweepLater();

        // Assert
        removed.Should().Be(0);
        File.Exists(Path.Combine(stale, "uninstall.exe")).Should().BeTrue();
    }

    [WindowsFact]
    public void A_directory_with_a_file_held_open_is_kept_whole()
    {
        // Arrange — two files; the second one is held open, as a live copy's image is.
        var stale = Candidate(null, "a-uninstall.exe", "z-original-uninstall.exe");
        int removed;

        // Act
        using (new FileStream(Path.Combine(stale, "z-original-uninstall.exe"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            removed = SweepLater();
        }

        // Assert
        removed.Should().Be(0);
        File.Exists(Path.Combine(stale, "a-uninstall.exe")).Should().BeTrue(
            "the probe completes before anything is deleted, so one busy file keeps every file");
        File.Exists(Path.Combine(stale, "z-original-uninstall.exe")).Should().BeTrue();
        _log.Should().Contain(l => l.Contains("still in use", StringComparison.Ordinal));
    }

    [WindowsFact]
    public void A_young_directory_is_kept()
    {
        // Arrange — created just now: possibly an uninstall staging its copy.
        var young = Candidate(null, "uninstall.exe");

        // Act
        var removed = StaleStagingSweep.Sweep(_root, requireAdminOnly: false, DateTime.UtcNow, Age, null, Log);

        // Assert
        removed.Should().Be(0);
        Directory.Exists(young).Should().BeTrue();
    }

    [WindowsFact]
    public void The_directory_this_process_runs_from_is_skipped()
    {
        // Arrange
        var own = Candidate(null, "uninstall.exe");

        // Act
        var removed = SweepLater(ownDirectory: own);

        // Assert
        removed.Should().Be(0);
        Directory.Exists(own).Should().BeTrue();
    }

    [WindowsFact]
    public void Under_the_elevated_root_a_directory_that_is_not_administrator_only_is_kept()
    {
        // Arrange — any user may create a child of %ProgramData%; only an admin-only one is ours.
        var planted = Candidate(null, "uninstall.exe");

        // Act
        var removed = SweepLater(requireAdminOnly: true);

        // Assert
        removed.Should().Be(0);
        Directory.Exists(planted).Should().BeTrue();
    }

    [WindowsFact]
    public void A_file_that_cannot_be_opened_for_write_keeps_its_directory()
    {
        // Arrange
        var stale = Candidate(null, "uninstall.exe");
        var file = Path.Combine(stale, "uninstall.exe");
        File.SetAttributes(file, FileAttributes.ReadOnly);

        try
        {
            // Act
            var act = () => SweepLater();

            // Assert
            act.Should().NotThrow();
            File.Exists(file).Should().BeTrue();
        }
        finally
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
    }

    [Fact]
    public void The_sweep_never_throws_on_a_missing_or_unusable_root()
    {
        // Arrange
        var missing = Path.Combine(_root, "does-not-exist");
        var aFile = Path.Combine(_root, "a-file");
        File.WriteAllText(aFile, "x");

        // Act
        var onMissing = () => StaleStagingSweep.Sweep(missing, false, DateTime.UtcNow, Age, null, Log);
        var onFile = () => StaleStagingSweep.Sweep(aFile, false, DateTime.UtcNow, Age, null, Log);
        var onEmpty = () => StaleStagingSweep.Sweep(string.Empty, false, DateTime.UtcNow, Age, null, Log);

        // Assert
        onMissing.Should().NotThrow().Which.Should().Be(0);
        onFile.Should().NotThrow().Which.Should().Be(0);
        onEmpty.Should().NotThrow().Which.Should().Be(0);
    }

    [WindowsFact]
    public void The_sweep_never_throws_when_its_log_sink_throws()
    {
        // Arrange
        var stale = Candidate(null, "uninstall.exe");
        var busy = Candidate(null, "uninstall.exe");
        Action<string, bool> throwing = (_, _) => throw new InvalidOperationException("sink");

        // Act
        Func<int> act;
        using (new FileStream(Path.Combine(busy, "uninstall.exe"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            act = () => StaleStagingSweep.Sweep(
                _root, false, DateTime.UtcNow + TimeSpan.FromMinutes(10), Age, null, throwing);

            // Assert
            act.Should().NotThrow().Which.Should().Be(1);
        }
        Directory.Exists(stale).Should().BeFalse();
        Directory.Exists(busy).Should().BeTrue();
    }

    private static void CreateJunction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var process = Process.Start(psi)!;
        process.WaitForExit(30_000);
        process.ExitCode.Should().Be(0, "creating a junction requires no privilege on Windows");
        new DirectoryInfo(link).LinkTarget.Should().NotBeNull("the fixture must really be a junction");
    }
}
