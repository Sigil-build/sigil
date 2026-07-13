using System;
using System.IO;
using FluentAssertions;
using SigilBuild.Installer.Host.Branding;
using SigilBuild.Installer.Host.ViewModels;
using Xunit;

namespace SigilBuild.Installer.Host.Tests;

/// <summary>
/// P7 host coverage: the Failed screen offers an "Open log" affordance only when a
/// /LOG file actually exists on disk. <see cref="InstallerViewModel.HasLog"/> gates
/// the button's visibility.
/// </summary>
public sealed class FailedLogAffordanceTests
{
    [Fact]
    public void HasLog_is_false_without_a_log_path()
    {
        var vm = new InstallerViewModel(new BrandTokens());
        vm.LogFilePath.Should().BeNull();
        vm.HasLog.Should().BeFalse();
    }

    [Fact]
    public void HasLog_is_false_when_the_path_does_not_exist()
    {
        var vm = new InstallerViewModel(new BrandTokens())
        {
            LogFilePath = Path.Combine(Path.GetTempPath(), "sigil-nonexistent-" + Guid.NewGuid().ToString("N") + ".log"),
        };
        vm.HasLog.Should().BeFalse();
    }

    [Fact]
    public void HasLog_is_true_when_the_log_file_exists()
    {
        var path = Path.Combine(Path.GetTempPath(), "sigil-hud-" + Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllText(path, "[log]\n");
        try
        {
            var vm = new InstallerViewModel(new BrandTokens()) { LogFilePath = path };
            vm.HasLog.Should().BeTrue();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
