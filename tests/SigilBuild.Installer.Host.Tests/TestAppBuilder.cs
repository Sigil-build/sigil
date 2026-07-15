using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Headless;
using SigilBuild.Installer.Host;
using SigilBuild.Wrapper.Core.Localization;
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

    // P9 Task 13 fallout: production field initializers (InstallerViewModel,
    // UninstallViewModel, FieldViewModel, BrandTokens) now read
    // SessionLanguage.Current at construction time. In Release that guard falls
    // back to Lang.En + logs; in Debug it throws — deliberately (see
    // SessionLanguage.Current's remarks). ~20 of this assembly's test classes
    // construct those types without ever calling SessionLanguage.Set, so under a
    // Debug test run they hit the throw. Establish the same English default here,
    // once, at assembly load, so Debug and Release runs behave identically. This
    // is exactly the sanctioned one-time-bootstrap use of ModuleInitializer that
    // CA2255 exists to flag elsewhere.
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255", Justification = "Test-assembly bootstrap: establishes the SessionLanguage default once at load, mirroring the Release-mode fallback so Debug test runs behave the same.")]
    internal static void InitializeSessionLanguage() => SessionLanguage.Set(Lang.En);
}
