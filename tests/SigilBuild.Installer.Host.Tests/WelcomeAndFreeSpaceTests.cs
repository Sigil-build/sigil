// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using FluentAssertions;
using SigilBuild.Installer.Host.Branding;
using SigilBuild.Installer.Host.ViewModels;
using Xunit;

namespace SigilBuild.Installer.Host.Tests;

/// <summary>
/// The Welcome screen shows the version being installed and the location screen shows
/// free space on the target drive, degrading to nothing when it cannot be read. (R99)
/// </summary>
public sealed class WelcomeAndFreeSpaceTests
{
    private const long Gb = 1024L * 1024 * 1024;

    private static InstallerViewModel Vm(string version = "2.5.0") =>
        new(new BrandTokens { AppName = "Acme Studio", AppVersion = version });

    [Fact]
    public void Welcome_version_line_carries_the_app_version()
    {
        // Arrange
        var vm = Vm("2.5.0");

        // Act / Assert
        vm.HasWelcomeVersion.Should().BeTrue();
        vm.WelcomeVersion.Should().Contain("2.5.0");
    }

    [Fact]
    public void Welcome_version_line_is_hidden_when_no_version_is_declared()
    {
        var vm = Vm(string.Empty);

        vm.HasWelcomeVersion.Should().BeFalse();
        vm.WelcomeVersion.Should().BeEmpty();
    }

    [Fact]
    public void Free_space_reads_the_drive_of_the_chosen_path_and_updates_as_it_changes()
    {
        // Arrange
        var probed = new List<string>();
        var vm = Vm();
        vm.SetFreeSpaceProbe(p =>
        {
            probed.Add(p);
            return p.StartsWith("D:", StringComparison.Ordinal) ? 512L * 1024 * 1024 : 12 * Gb;
        });

        // Act
        vm.InstallPath = @"C:\Apps\Acme";
        var onC = vm.InstallPathFreeSpace;
        vm.InstallPath = @"D:\Apps\Acme";
        var onD = vm.InstallPathFreeSpace;

        // Assert
        vm.HasInstallPathFreeSpace.Should().BeTrue();
        onC.Should().Contain("C:").And.Contain("12");
        onD.Should().Contain("D:").And.Contain("512");
        probed.Should().Contain(new[] { @"C:\Apps\Acme", @"D:\Apps\Acme" });
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"relative\path")]
    public void Free_space_is_empty_for_a_blank_or_relative_path(string path)
    {
        // Arrange
        var vm = Vm();
        vm.SetFreeSpaceProbe(_ => 5 * Gb);

        // Act
        vm.InstallPath = path;

        // Assert
        vm.InstallPathFreeSpace.Should().BeEmpty();
        vm.HasInstallPathFreeSpace.Should().BeFalse();
    }

    [Fact]
    public void Free_space_is_empty_when_the_drive_is_unavailable_or_the_probe_throws()
    {
        // Arrange
        var vm = Vm();
        vm.SetFreeSpaceProbe(_ => null);

        // Act / Assert — unavailable drive
        vm.InstallPath = @"Z:\Nope";
        vm.HasInstallPathFreeSpace.Should().BeFalse();

        // Act / Assert — probe failure
        vm.SetFreeSpaceProbe(_ => throw new InvalidOperationException("boom"));
        vm.InstallPath = @"Y:\Nope";
        vm.InstallPathFreeSpace.Should().BeEmpty();
    }

    [Fact]
    public void Default_probe_reports_something_for_the_temp_drive_and_nothing_for_a_unc_path()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Arrange
        var vm = Vm();

        // Act
        vm.InstallPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sigil-freespace-probe");
        var local = vm.InstallPathFreeSpace;
        vm.InstallPath = @"\\no-such-host\share\app";

        // Assert
        local.Should().NotBeEmpty();
        vm.InstallPathFreeSpace.Should().BeEmpty();
    }
}
