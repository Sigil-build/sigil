using System.Runtime.InteropServices;
using FluentAssertions;
using SigilBuild.Wrapper.Core.Localization;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Localization;

public class OsUiLanguageTests
{
    [Fact]
    public void Preferences_OnWindows_AreWellFormedTags()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            OsUiLanguage.Preferences().Should().BeEmpty();
            return;
        }

        var prefs = OsUiLanguage.Preferences();
        prefs.Should().NotBeEmpty("a Windows machine always reports at least one UI language");
        prefs.Should().OnlyContain(t => SigilBuild.Core.Manifest.LanguageTag.IsValid(t));
    }

    [Fact]
    public void Primary_IsFirstPreference_OrEmpty()
    {
        var prefs = OsUiLanguage.Preferences();
        OsUiLanguage.Primary().Should().Be(prefs.Count > 0 ? prefs[0] : string.Empty);
    }

    [Fact]
    public void Primary_IsTotal_NeverNullNeverThrows()
    {
        var act = () => OsUiLanguage.Primary();
        act.Should().NotThrow("ADR-008 §1.2 requires locale() to be total");
        OsUiLanguage.Primary().Should().NotBeNull();
    }
}
