using System;
using System.IO;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Engine;

/// <summary>
/// T12 per-scope layout: the machine vs user mapping for install root, state /
/// journal root, PATH scope, and shortcut folders — the parameterization that
/// replaces hardcoded machine paths.
/// </summary>
public sealed class ScopeLayoutTests
{
    [Fact]
    public void Machine_layout_targets_program_files_and_program_data()
    {
        var layout = ScopeLayout.For(InstallScope.Machine);

        layout.IsMachine.Should().BeTrue();
        layout.Name.Should().Be("machine");
        layout.EnvScope.Should().Be("machine");
        layout.InstallRoot.Should().Be(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        layout.StateRoot.Should().Be(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
    }

    [Fact]
    public void User_layout_targets_localappdata_programs_and_localappdata()
    {
        var layout = ScopeLayout.For(InstallScope.User);

        layout.IsMachine.Should().BeFalse();
        layout.Name.Should().Be("user");
        layout.EnvScope.Should().Be("user");
        layout.InstallRoot.Should().Be(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs"));
        layout.StateRoot.Should().Be(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    }

    [Fact]
    public void Auto_is_treated_as_user()
    {
        ScopeLayout.For(InstallScope.Auto).Scope.Should().Be(InstallScope.User);
    }

    [Fact]
    public void Machine_shortcut_folders_are_the_common_all_users_folders()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var machine = ScopeLayout.For(InstallScope.Machine);
        var user = ScopeLayout.For(InstallScope.User);

        machine.DesktopFolder.Should().Be(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));
        machine.StartMenuFolder.Should().Be(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu));

        user.DesktopFolder.Should().Be(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
        user.StartMenuFolder.Should().Be(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu));
    }
}
