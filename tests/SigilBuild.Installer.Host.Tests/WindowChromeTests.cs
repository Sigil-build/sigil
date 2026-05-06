using Avalonia.Headless.XUnit;
using FluentAssertions;
using SigilBuild.Installer.Host.Branding;
using SigilBuild.Installer.Host.ViewModels;
using SigilBuild.Installer.Host.Views;
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
}
