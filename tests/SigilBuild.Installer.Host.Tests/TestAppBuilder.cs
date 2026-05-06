using Avalonia;
using Avalonia.Headless;
using SigilBuild.Installer.Host;

[assembly: Avalonia.Headless.AvaloniaTestApplication(typeof(SigilBuild.Installer.Host.Tests.TestAppBuilder))]

namespace SigilBuild.Installer.Host.Tests;

public class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
