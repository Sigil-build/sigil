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

    /// <summary>
    /// Register row R52. Containment (<c>InstallDirResolver.IsContained</c>) accepted
    /// <c>%ProgramFiles(x86)%</c> while <see cref="ScopeLayout"/> modelled only
    /// <c>%ProgramFiles%</c>, so the PERMITTED destinations and the DEFAULT destination
    /// were two independently-maintained facts. This asserts they are one fact: every
    /// root containment accepts is a root the layout itself declares.
    /// </summary>
    /// <remarks>
    /// The parent-commit form of this test replaced <c>layout.InstallRoots</c> with
    /// <c>new[] { layout.InstallRoot }</c> — the roots <c>ScopeLayout</c> modelled
    /// before the fix — and failed on exactly this path.
    /// </remarks>
    [Fact]
    public void Machine_layout_declares_every_root_containment_accepts()
    {
        var layout = ScopeLayout.For(InstallScope.Machine);
        var x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (string.IsNullOrEmpty(x86))
        {
            throw new InvalidOperationException(
                "this test needs a 64-bit Windows host with a %ProgramFiles(x86)% root");
        }

        var underX86 = Path.Combine(x86, "Contoso");

        InstallDirResolver.IsContained(layout, underX86).Should().BeTrue(
            "lane S2 widened machine containment to both Program Files roots");

        IsUnderAnyDeclaredRoot(layout, underX86).Should().BeTrue(
            "ScopeLayout must declare every root containment accepts — otherwise the " +
            "default install destination and the permitted install destinations are two " +
            "facts that drift apart silently (R52). Declared roots: {0}",
            string.Join(", ", layout.InstallRoots));
    }

    [Fact]
    public void User_layout_declares_every_root_containment_accepts()
    {
        var layout = ScopeLayout.For(InstallScope.User);
        var underProfile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Contoso");

        InstallDirResolver.IsContained(layout, underProfile).Should().BeTrue(
            "user scope crosses no privilege boundary, so the whole profile is permitted");

        IsUnderAnyDeclaredRoot(layout, underProfile).Should().BeTrue(
            "ScopeLayout must declare every root containment accepts (R52). Declared roots: {0}",
            string.Join(", ", layout.InstallRoots));
    }

    [Fact]
    public void The_default_install_root_is_the_first_declared_root()
    {
        foreach (var scope in new[] { InstallScope.Machine, InstallScope.User })
        {
            var layout = ScopeLayout.For(scope);
            layout.InstallRoots.Should().NotBeEmpty();
            layout.InstallRoots[0].Should().Be(
                layout.InstallRoot,
                "the default destination must be one of the permitted destinations, and " +
                "callers rely on the first entry being the default ({0} scope)",
                layout.Name);
        }
    }

    private static bool IsUnderAnyDeclaredRoot(ScopeLayout layout, string candidate)
    {
        foreach (var root in layout.InstallRoots)
        {
            if (!string.IsNullOrWhiteSpace(root)
                && PathContainment.IsUnderWithoutTraversal(root, candidate))
            {
                return true;
            }
        }
        return false;
    }
}
