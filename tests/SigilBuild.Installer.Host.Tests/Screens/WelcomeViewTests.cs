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

public class WelcomeViewTests
{
    [AvaloniaFact]
    public void Welcome_BindsAppName()
    {
        var vm = new InstallerViewModel(new BrandTokens { AppName = "Acme Pro" });
        var view = new WelcomeView { DataContext = vm };

        view.FindControl<TextBlock>("Heading")!.Text.Should().Contain("Acme Pro");
    }
}
