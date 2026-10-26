// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

namespace SigilBuild.Wrapper.Tests.Engine;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Cli;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Tests.Helpers;
using Xunit;

/// <summary>
/// <c>uninstall.exe</c> runs the uninstall from a private temporary copy of itself so the
/// copy can remove the install directory, the original uninstaller included, instead of
/// leaving it until the next reboot. These pin the pieces that can be exercised without
/// launching a real uninstaller: when the relaunch happens, what the copy is told, how the
/// copy is staged and verified, what the copy accepts as its original, and how the
/// original — still the running image of the waiting parent — leaves the install
/// directory. (R106)
/// </summary>
public sealed class UninstallerRelaunchTests
{
    private const string Original = @"C:\Program Files\Acme\uninstall.exe";

    // --- when ---

    [Fact]
    public void A_stamped_uninstall_exe_in_uninstall_mode_relaunches()
    {
        // Act
        var relaunch = UninstallerRelaunch.ShouldRelaunch(
            WrapperMode.Uninstall, stamped: true, relaunchedFrom: null, imagePath: Original);

        // Assert
        relaunch.Should().BeTrue();
    }

    [Theory]
    [InlineData(WrapperMode.Install, true, null, Original)]
    [InlineData(WrapperMode.Update, true, null, Original)]
    [InlineData(WrapperMode.Uninstall, false, null, Original)]
    [InlineData(WrapperMode.Uninstall, true, Original, @"C:\Temp\sigil-uninstall-1\uninstall.exe")]
    [InlineData(WrapperMode.Uninstall, true, null, @"C:\Users\me\Downloads\Acme-Setup.exe")]
    [InlineData(WrapperMode.Uninstall, true, null, null)]
    public void Nothing_else_relaunches(WrapperMode mode, bool stamped, string? relaunchedFrom, string? image)
    {
        // Act
        var relaunch = UninstallerRelaunch.ShouldRelaunch(mode, stamped, relaunchedFrom, image);

        // Assert
        relaunch.Should().BeFalse(
            "only the stamped uninstall.exe relaunches, once — never an install, never the " +
            "un-stamped runtime, never setup.exe /Uninstall, and never the copy itself");
    }

    // --- what the copy is told ---

    [Fact]
    public void The_copy_gets_the_original_arguments_plus_an_explicit_mode_and_its_original()
    {
        // Arrange
        var args = new[] { "/S", "/currentuser", "/LOG=C:\\logs\\u.log" };

        // Act
        var copyArgs = UninstallerRelaunch.BuildCopyArgs(args, Original);

        // Assert
        copyArgs.Should().Equal(
            "/S", "/currentuser", "/LOG=C:\\logs\\u.log", "/Uninstall",
            UninstallerRelaunch.Switch + Original);
    }

    [Fact]
    public void An_explicit_uninstall_flag_is_kept_once_and_an_inbound_relaunch_token_is_dropped()
    {
        // Arrange
        var args = new[] { "/uninstall", "/relaunchedfrom=C:\\elsewhere\\uninstall.exe", "/allusers" };

        // Act
        var copyArgs = UninstallerRelaunch.BuildCopyArgs(args, Original);

        // Assert
        copyArgs.Should().Equal("/uninstall", "/allusers", UninstallerRelaunch.Switch + Original);
    }

    [Fact]
    public void The_copy_parses_its_original_and_renders_it_in_the_log_header()
    {
        // Arrange
        var args = UninstallerRelaunch.BuildCopyArgs(new[] { "/S" }, Original);

        // Act
        var parsed = CommandLineParser.Parse(args, Array.Empty<ParameterDefinition>());

        // Assert
        parsed.Mode.Should().Be(WrapperMode.Uninstall);
        parsed.RelaunchedFrom.Should().Be(Original);
        parsed.AuditSafeRendering().Should().Contain("/RelaunchedFrom=" + Original);
    }

    [Fact]
    public void An_empty_relaunch_token_is_a_usage_error()
    {
        // Act
        var act = () => CommandLineParser.Parse(
            new[] { "/Uninstall", UninstallerRelaunch.Switch }, Array.Empty<ParameterDefinition>());

        // Assert
        act.Should().Throw<UsageException>().WithMessage("*requires a path*");
    }

    [Fact]
    public void An_ordinary_run_carries_no_original()
    {
        // Act
        var parsed = CommandLineParser.Parse(new[] { "/S", "/Uninstall" }, Array.Empty<ParameterDefinition>());

        // Assert
        parsed.RelaunchedFrom.Should().BeNull();
        parsed.AuditSafeRendering().Should().NotContain("RelaunchedFrom");
    }

    [Fact]
    public void A_relative_log_path_is_resolved_against_the_starting_directory()
    {
        // Arrange
        var args = new[] { "/S", "/LOG=logs\\u.log", "/LOG", "/LOG=D:\\abs.log" };

        // Act
        var rewritten = UninstallerRelaunch.AbsolutizeLogPath(args, @"C:\start");

        // Assert
        rewritten.Should().Equal("/S", @"/LOG=C:\start\logs\u.log", "/LOG", "/LOG=D:\\abs.log");
    }

    [Theory]
    [InlineData(@"C:\Program Files\Acme", @"C:\Program Files\Acme", true)]
    [InlineData(@"C:\Program Files\Acme\bin", @"C:\Program Files\Acme", true)]
    [InlineData(@"C:\Program Files\AcmeTools", @"C:\Program Files\Acme", false)]
    [InlineData(@"C:\Windows\System32", @"C:\Program Files\Acme", false)]
    public void Leaving_the_install_directory_means_leaving_everything_beneath_it(
        string cwd, string installDir, bool within)
    {
        // Act / Assert
        UninstallerRelaunch.IsWithin(cwd, installDir).Should().Be(within);
    }

    // --- staging the copy ---

    [Fact]
    public void The_copy_is_staged_byte_identical_in_a_fresh_private_directory_and_held_against_writes()
    {
        // Arrange
        using var scratch = new TempDir();
        using var siting = SecureStaging.UseSitingForTesting(scratch.Path);
        var source = Path.Combine(scratch.Path, "uninstall.exe");
        var bytes = Enumerable.Range(0, 200_000).Select(i => (byte)(i * 7)).ToArray();
        File.WriteAllBytes(source, bytes);
        using var staging = SecureStaging.Create(UninstallerRelaunch.StagingPurpose, (_, _) => { });

        // Act
        var copy = UninstallerRelaunch.StageVerifiedCopy(source, staging, out var held);

        // Assert
        using (held)
        {
            Path.GetFileName(copy).Should().Be("uninstall.exe", "the copy keeps the uninstaller's name");
            Path.GetDirectoryName(copy).Should().Be(staging.Directory);
            Path.GetFileName(staging.Directory).Should().StartWith("sigil-uninstall-");
            File.ReadAllBytes(copy).Should().Equal(bytes);

            var overwrite = () => new FileStream(copy, FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose();
            overwrite.Should().Throw<IOException>(
                "the verified handle denies writes until the copy has been started (R5)");
        }
    }

    // --- what the copy accepts as its original ---

    [Fact]
    public void The_original_is_accepted_when_it_is_a_byte_identical_uninstaller_elsewhere()
    {
        // Arrange
        using var installDir = new TempDir();
        using var copyDir = new TempDir();
        var original = Path.Combine(installDir.Path, "uninstall.exe");
        var own = Path.Combine(copyDir.Path, "uninstall.exe");
        File.WriteAllBytes(original, new byte[] { 1, 2, 3 });
        File.Copy(original, own);

        // Act
        var verified = UninstallerRelaunch.VerifyOriginal(original, own);

        // Assert
        verified.Should().Be(original);
    }

    [Fact]
    public void A_claimed_original_that_is_not_this_uninstaller_is_refused()
    {
        // Arrange
        using var installDir = new TempDir();
        using var copyDir = new TempDir();
        var own = Path.Combine(copyDir.Path, "uninstall.exe");
        File.WriteAllBytes(own, new byte[] { 1, 2, 3 });
        var different = Path.Combine(installDir.Path, "uninstall.exe");
        File.WriteAllBytes(different, new byte[] { 1, 2, 4 });
        var wrongName = Path.Combine(installDir.Path, "app.exe");
        File.WriteAllBytes(wrongName, new byte[] { 1, 2, 3 });
        var sameDir = Path.Combine(copyDir.Path, "UNINSTALL.EXE");

        // Act / Assert
        UninstallerRelaunch.VerifyOriginal(different, own).Should().BeNull("other bytes are another program");
        UninstallerRelaunch.VerifyOriginal(wrongName, own).Should().BeNull("the original is an uninstall.exe");
        UninstallerRelaunch.VerifyOriginal(sameDir, own).Should().BeNull("the original is not the copy itself");
        UninstallerRelaunch.VerifyOriginal(@"relative\uninstall.exe", own).Should().BeNull();
        UninstallerRelaunch.VerifyOriginal(Path.Combine(installDir.Path, "missing", "uninstall.exe"), own)
            .Should().BeNull();
        UninstallerRelaunch.VerifyOriginal(null, own).Should().BeNull();
    }

    // --- the original leaves the install directory ---

    /// <summary>
    /// The decisive case: the original is the RUNNING image of the waiting relaunch
    /// parent, so it cannot be deleted — but it can be renamed, and renaming it into the
    /// copy's private directory empties the install directory now rather than at the next
    /// reboot.
    /// </summary>
    [WindowsFact]
    public void A_running_original_is_moved_out_of_the_install_directory_into_the_copys_directory()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Arrange
        using var installDir = new TempDir();
        using var copyDir = new TempDir();
        using var parent = ImageInDirProcess.Start(installDir.Path, "uninstall.exe");
        try
        {
            // Act
            var outcome = SelfDelete.Remove(
                parent.ImagePath,
                runningImagePath: Path.Combine(copyDir.Path, "uninstall.exe"),
                setAsideDirectory: copyDir.Path);

            // Assert
            outcome.Should().Be(SelfDeleteOutcome.SetAside);
            Directory.EnumerateFileSystemEntries(installDir.Path).Should().BeEmpty(
                "nothing of the uninstaller is left in the install directory");
            Directory.EnumerateFiles(copyDir.Path, "original-*.exe").Should().ContainSingle();
        }
        finally
        {
            parent.Kill();
        }
    }

    [Fact]
    public void A_file_that_can_be_deleted_is_deleted_even_with_a_set_aside_directory()
    {
        // Arrange
        using var installDir = new TempDir();
        using var copyDir = new TempDir();
        var target = Path.Combine(installDir.Path, "uninstall.exe");
        File.WriteAllBytes(target, new byte[] { 7 });

        // Act
        var outcome = SelfDelete.Remove(
            target, runningImagePath: Path.Combine(copyDir.Path, "uninstall.exe"), setAsideDirectory: copyDir.Path);

        // Assert
        outcome.Should().Be(SelfDeleteOutcome.Deleted);
        Directory.EnumerateFileSystemEntries(copyDir.Path).Should().BeEmpty();
    }

    [WindowsFact]
    public void The_copys_directory_is_removed_once_the_copy_has_exited()
    {
        // Arrange
        using var scratch = new TempDir();
        var dir = Path.Combine(scratch.Path, "sigil-uninstall-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "uninstall.exe"), new byte[] { 1 });
        var lines = new List<string>();

        // Act
        UninstallerRelaunch.CleanUpCopyDirectory(dir, (m, _) => lines.Add(m));

        // Assert
        Directory.Exists(dir).Should().BeFalse();
        lines.Should().BeEmpty("nothing was left to schedule");
    }
}
