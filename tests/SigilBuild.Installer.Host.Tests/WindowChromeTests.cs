// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using SigilBuild.Installer.Host.Branding;
using SigilBuild.Installer.Host.ViewModels;
using SigilBuild.Installer.Host.Views;
using SigilBuild.Wrapper.Core.Localization;
using Xunit;

namespace SigilBuild.Installer.Host.Tests;

public class WindowChromeTests
{
    [AvaloniaFact]
    public void InstallerWindow_HasFixedSize_800x500()
    {
        var window = new InstallerWindow { DataContext = new InstallerViewModel(new BrandTokens()) };
        window.Show();

        window.Width.Should().Be(800);
        window.Height.Should().Be(500);
        window.CanResize.Should().BeFalse();
    }

    [AvaloniaFact]
    public void InstallerWindow_StartsOnWelcomeScreen()
    {
        var vm = new InstallerViewModel(new BrandTokens());
        vm.CurrentStep.Should().Be(InstallerStep.Welcome);
    }

    /// <summary>
    /// The finished wizard must offer a way out. It did not: the footer showed
    /// Back / Next / Cancel on every screen, Back and Next disabled on Finish, and
    /// Cancel wired to a method that returns false there by design — so the only
    /// enabled button did nothing, and the window could be closed only from the
    /// taskbar or Task Manager. Asserted at the window rather than the view model
    /// because the view model's IsTerminal was never the missing part; the binding
    /// was.
    /// </summary>
    [AvaloniaFact]
    public void InstallerWindow_OnFinish_ShowsCloseAndHidesTheNavigationFooter()
    {
        var vm = new InstallerViewModel(new BrandTokens());
        var window = new InstallerWindow { DataContext = vm };
        window.Show();

        vm.CurrentStep = InstallerStep.Finish;

        var buttons = window.GetVisualDescendants().OfType<Button>().ToList();
        buttons.Should().NotBeEmpty("the footer must actually be realised for this to mean anything");

        var visible = buttons.Where(b => b.IsVisible).ToList();
        visible.Should().ContainSingle(
            "a terminal screen offers exactly one action — leave")
            .Which.Content.Should().Be(S.NavClose);
    }

    /// <summary>
    /// A terminal screen has nothing to abandon, so the title bar's X must close
    /// outright. A wizard whose X stops working is a worse trap than the one that
    /// had no X at all.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(InstallerStep.Finish)]
    [InlineData(InstallerStep.Failed)]
    [InlineData(InstallerStep.DowngradeBlocked)]
    public void InstallerWindow_CloseGesture_OnTerminalScreen_ClosesOutright(InstallerStep step)
    {
        var vm = new InstallerViewModel(new BrandTokens());
        var window = new InstallerWindow { DataContext = vm };
        window.Show();
        vm.CurrentStep = step;

        window.Close();
        Dispatcher.UIThread.RunJobs();

        window.IsVisible.Should().BeFalse(
            "the {0} screen is where the wizard is finished with the user — the X must let them go", step);
    }

    /// <summary>
    /// Before the terminal screens the X must ask, not act. It sits a few pixels
    /// from the buttons the user meant to press, and an unconfirmed close throws
    /// away whatever they had already chosen.
    /// </summary>
    [AvaloniaFact]
    public void InstallerWindow_CloseGesture_BeforeFinish_AsksInsteadOfClosing()
    {
        var vm = new InstallerViewModel(new BrandTokens());
        var window = new InstallerWindow { DataContext = vm };
        window.Show();

        window.Close();
        Dispatcher.UIThread.RunJobs();

        window.IsVisible.Should().BeTrue(
            "the close must wait on the confirmation dialog — an unanswered prompt is not consent");
    }

    [AvaloniaFact]
    public void InstallerWindow_BeforeFinish_ShowsTheNavigationFooterAndNoClose()
    {
        var vm = new InstallerViewModel(new BrandTokens());
        var window = new InstallerWindow { DataContext = vm };
        window.Show();

        var visible = window.GetVisualDescendants().OfType<Button>()
            .Where(b => b.IsVisible)
            .Select(b => b.Content as string)
            .ToList();

        visible.Should().Contain(S.NavCancel);
        visible.Should().NotContain(S.NavClose, "there is nothing to close yet — the install has not run");
    }
}
