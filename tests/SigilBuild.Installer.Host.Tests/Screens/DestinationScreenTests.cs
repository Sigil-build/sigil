using System;
using System.IO;
using FluentAssertions;
using SigilBuild.Installer.Host.Branding;
using SigilBuild.Installer.Host.ViewModels;
using Xunit;

namespace SigilBuild.Installer.Host.Tests.Screens;

/// <summary>
/// VM-level coverage for the Destination screen (T13): path validation gating Next
/// inline, and the scope toggle recomputing the default path.
/// </summary>
public sealed class DestinationScreenTests
{
    private static InstallerViewModel AtDestination()
    {
        var vm = new InstallerViewModel(new BrandTokens { AppName = "Example" });
        vm.Next(); // Welcome → InstallOptions (destination)
        vm.CurrentStep.Should().Be(InstallerStep.InstallOptions);
        return vm;
    }

    [Fact]
    public void Relative_path_blocks_next_with_inline_error()
    {
        var vm = AtDestination();
        vm.InstallPath = Path.Combine("relative", "path");

        vm.Next();

        vm.CurrentStep.Should().Be(InstallerStep.InstallOptions, "an invalid path must block advancing");
        vm.HasInstallPathError.Should().BeTrue();
        vm.InstallPathError.Should().Contain("absolute");
    }

    [Fact]
    public void Blank_path_blocks_next()
    {
        var vm = AtDestination();
        vm.InstallPath = "   ";

        vm.ValidateDestination().Should().BeFalse();
        vm.HasInstallPathError.Should().BeTrue();
    }

    [Fact]
    public void A_file_path_is_rejected()
    {
        var file = Path.Combine(Path.GetTempPath(), "sigil-t13-" + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(file, "x");
        try
        {
            var vm = AtDestination();
            vm.InstallPath = file;

            vm.ValidateDestination().Should().BeFalse();
            vm.InstallPathError.Should().Contain("file");
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Valid_absolute_writable_path_clears_the_error_and_advances()
    {
        var vm = AtDestination();
        vm.InstallPath = Path.Combine(Path.GetTempPath(), "SigilT13", Guid.NewGuid().ToString("N"));

        vm.Next();

        vm.HasInstallPathError.Should().BeFalse();
        vm.CurrentStep.Should().Be(InstallerStep.Installing);
    }

    [Fact]
    public void Scope_toggle_recomputes_the_default_path()
    {
        var vm = new InstallerViewModel(new BrandTokens { AppName = "Example" });
        var userPath = Path.Combine("C:", "Users", "me", "Example");
        var machinePath = Path.Combine("C:", "Program Files", "Example");

        vm.ConfigureDestination(
            scopeSelectable: true,
            defaultPathResolver: isMachine => isMachine ? machinePath : userPath,
            initialPath: userPath);

        vm.ScopeSelectable.Should().BeTrue();
        vm.InstallPath.Should().Be(userPath);
        vm.IsUserScope.Should().BeTrue();

        vm.IsMachineScope = true;

        vm.InstallPath.Should().Be(machinePath, "selecting all-users swaps the path to Program Files");
        vm.IsUserScope.Should().BeFalse();

        vm.IsUserScope = true;
        vm.InstallPath.Should().Be(userPath);
        vm.IsMachineScope.Should().BeFalse();
    }
}
