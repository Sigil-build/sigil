// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using SigilBuild.Installer.Host.Branding;
using SigilBuild.Installer.Host.ViewModels;
using SigilBuild.Installer.Host.Views.Screens;
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
}
