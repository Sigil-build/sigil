// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using FluentAssertions;
using SigilBuild.Installer.Host.Branding;
using SigilBuild.Installer.Host.ViewModels;
using SigilBuild.Installer.Host.Views.Screens;
using SigilBuild.Wrapper.Engine;
using Xunit;

namespace SigilBuild.Installer.Host.Tests.Screens;

public class InstallOptionsViewTests
{
    [AvaloniaFact]
    public void Default_InstallPath_PointsAtProgramFiles()
    {
        var vm = new InstallerViewModel(new BrandTokens { AppName = "Example" });
        var view = new InstallOptionsView { DataContext = vm };

        var box = view.FindControl<TextBox>("InstallPath")!;
        box.Text.Should().Contain("Example");
    }

    [AvaloniaFact]
    public void Upgrade_banner_is_rendered_exactly_once()
    {
        // Arrange
        var vm = new InstallerViewModel(new BrandTokens { AppName = "Example", AppVersion = "2.0.0" });
        vm.SetUpgradeState(UpgradeAction.Upgrade, "1.0.0");
        var view = new InstallOptionsView { DataContext = vm };

        // Act
        var banners = view.GetLogicalDescendants().OfType<TextBlock>()
            .Where(t => t.Text == vm.UpgradeNotice)
            .ToList();

        // Assert
        vm.UpgradeNotice.Should().NotBeEmpty();
        banners.Should().HaveCount(1, "the upgrade banner must not be duplicated (R83)");
    }
}
