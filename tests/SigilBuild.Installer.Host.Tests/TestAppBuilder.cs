using Avalonia;
using Avalonia.Headless;
using SigilBuild.Installer.Host;
using Xunit;

[assembly: Avalonia.Headless.AvaloniaTestApplication(typeof(SigilBuild.Installer.Host.Tests.TestAppBuilder))]

// P9: SessionLanguage (SigilBuild.Wrapper.Core.Localization) is process-wide static
// state now read from many production code paths exercised across this assembly's
// tests (BrandTokens defaults, InstallerViewModel, FieldViewModel, ...). Serializing
// test collections keeps ViewModelLocalizationTests' SetForTesting(Lang.Uk) from
// racing another collection's bare `new BrandTokens()` expecting the English default.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace SigilBuild.Installer.Host.Tests;

public class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
