// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FluentAssertions;
using SigilBuild.Installer.Host.Branding;
using SigilBuild.Installer.Host.ViewModels;
using SigilBuild.Installer.Host.Views;
using Xunit;

namespace SigilBuild.Installer.Host.Tests;

/// <summary>
/// The install wizard's rail ends with the same "Version x.y.z" footer as the
/// uninstall window's rail, and shows none when the app declares no version. (R113)
/// </summary>
public sealed class RailVersionFooterTests
{
    [AvaloniaFact]
    public void Rendered_rail_shows_the_version_being_installed()
    {
        // Arrange
        var vm = new InstallerViewModel(new BrandTokens { AppName = "Acme Studio", AppVersion = "0.5.7" });
        var window = new InstallerWindow { DataContext = vm };

        // Act
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Assert
        var footer = window.FindControl<TextBlock>("RailVersion")!;
        footer.IsVisible.Should().BeTrue();
        footer.Text.Should().Contain("0.5.7");
    }

    [AvaloniaFact]
    public void Rendered_rail_keeps_the_footer_on_every_screen()
    {
        // Arrange
        var vm = new InstallerViewModel(new BrandTokens { AppName = "Acme Studio", AppVersion = "0.5.7" });
        var window = new InstallerWindow { DataContext = vm };
        window.Show();

        // Act
        vm.CurrentStep = InstallerStep.Installing;
        vm.CurrentStep = InstallerStep.Finish;
        Dispatcher.UIThread.RunJobs();

        // Assert
        window.FindControl<TextBlock>("RailVersion")!.IsVisible.Should().BeTrue();
    }

    [AvaloniaFact]
    public void Rendered_rail_hides_the_footer_when_the_app_declares_no_version()
    {
        // Arrange
        var vm = new InstallerViewModel(new BrandTokens { AppName = "Acme Studio", AppVersion = "" });
        var window = new InstallerWindow { DataContext = vm };

        // Act
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Assert
        var footer = window.FindControl<TextBlock>("RailVersion")!;
        footer.IsVisible.Should().BeFalse();
        vm.VersionLine.Should().BeEmpty();
    }

    [Fact]
    public void The_footer_uses_the_uninstall_windows_catalog_string()
    {
        // Arrange
        var brand = new BrandTokens { AppName = "Acme Studio", AppVersion = "1.2.3" };

        // Act
        var install = new InstallerViewModel(brand).VersionLine;
        var uninstall = new UninstallViewModel(brand).VersionLine;

        // Assert
        install.Should().Be(uninstall).And.Contain("1.2.3");
    }
}
