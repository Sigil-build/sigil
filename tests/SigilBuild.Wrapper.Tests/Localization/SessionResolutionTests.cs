using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using SigilBuild.Wrapper.Cli;
using SigilBuild.Wrapper.Core.Localization;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Tests.Helpers;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Localization;

/// <summary>
/// Task 14: resolution at session start (design §4.6). Before this task nothing
/// called <see cref="LanguageResolver"/> — <see cref="SessionLanguage.Current"/>
/// was only ever the <see cref="Lang.En"/> default. These tests cover the resolver
/// chain itself (given, Task 7), the session-start wiring
/// (<see cref="InstallSession.ResolveSessionLanguage"/>), the Step 3b license-map
/// resolution, and the <see cref="SessionLanguage.OnUninitializedRead"/> wiring.
/// </summary>
[Collection("SessionLanguage")]
public sealed class SessionResolutionTests : IDisposable
{
    public void Dispose()
    {
        // Restore the assembly-wide English default (TestAssemblySetup's
        // ModuleInitializer) rather than leaving SessionLanguage unset — see
        // SessionLanguageTests.Dispose for why a bare ResetForTesting() here
        // would reintroduce the Debug-mode throw for whichever test runs next.
        SessionLanguage.SetForTesting(Lang.En);
        SessionLanguage.OnUninitializedRead = null;
    }

    [Fact]
    public void FixedManifestLanguage_OverridesLangFlag_WithoutFailing()
    {
        // Design §2.1: language is a display preference, not a trust boundary,
        // so this does NOT mirror T12's fixed-scope vs /allusers exit 64.
        var prefs = LanguageResolver.Preferences(manifestLanguage: "en", langFlag: "uk", osPreferences: new[] { "uk-UA" });

        LanguageResolver.MatchChrome(prefs).Should().Be(Lang.En);
    }

    [Fact]
    public void SystemLanguage_IsChromeLanguage_NotTopOsPreference()
    {
        // Design §4.3: with [de-DE, uk-UA] and en+uk chrome, locale() says de-DE
        // but system.language says uk, because uk is what the UI renders.
        var prefs = LanguageResolver.Preferences(null, null, new[] { "de-DE", "uk-UA" });

        LanguageResolver.MatchChrome(prefs).Should().Be(Lang.Uk);
    }

    [Fact]
    public void EngineProse_IsLocalized()
    {
        Strings.EngineRemovingPrevious(Lang.Uk).Should().Be("Вилучення попередньої версії");
    }

    // ── Session-start wiring: ResolveSessionLanguage sets SessionLanguage.Current ──

    [Fact]
    public void ResolveSessionLanguage_SetsSessionLanguage_FromLangFlag()
    {
        // No installer.language pin on the un-stamped Empty blob and no OS
        // preference override reachable in a unit test, so /lang is authoritative.
        var session = InstallSession.Create(new[] { "/silent", "/lang=uk" });

        var resolved = session.ResolveSessionLanguage();

        resolved.Should().Be(Lang.Uk);
        SessionLanguage.Current.Should().Be(Lang.Uk);
    }

    [Fact]
    public void ResolveSessionLanguage_ExposesThePreferencesItResolvedFrom()
    {
        var session = InstallSession.Create(new[] { "/silent", "/lang=uk" });

        session.ResolveSessionLanguage();

        session.LanguagePreferences.Should().ContainSingle().Which.Should().Be("uk");
    }

    // ── Step 3b: the license MAP resolves against the session's language, not just English ──

    [Fact]
    public void LicenseMap_ResolvesToUkrainian_UnderLangFlag()
    {
        var map = new Dictionary<string, string> { ["en"] = "EULA", ["uk"] = "Ліцензія" };
        var preferences = LanguageResolver.Preferences(manifestLanguage: null, langFlag: "uk", osPreferences: Array.Empty<string>());

        InstallerLicenseLoader.Resolve(map, preferences).Should().Be("Ліцензія");
    }

    [Fact]
    public void LicenseMap_ResolvesToEnglish_WithNoLangFlag()
    {
        var map = new Dictionary<string, string> { ["en"] = "EULA", ["uk"] = "Ліцензія" };
        var preferences = LanguageResolver.Preferences(manifestLanguage: null, langFlag: null, osPreferences: Array.Empty<string>());

        InstallerLicenseLoader.Resolve(map, preferences).Should().Be("EULA");
    }

    [Fact]
    public void LicenseMap_Null_ResolvesToNull()
    {
        // No embedded license in the blob at all — the License screen stays absent.
        InstallerLicenseLoader.Resolve(null, new[] { "uk" }).Should().BeNull();
    }

    // ── OnUninitializedRead: wired to the /LOG sink at session start (was dead) ──

    [Fact]
    public void OnUninitializedRead_LogsToInstallLog_WhenCurrentReadBeforeResolution()
    {
        using var tmp = new TempDir();
        var logPath = Path.Combine(tmp.Path, "lang.log");

        // Wires SessionLanguage.OnUninitializedRead to THIS session's /LOG sink,
        // then sets Current — mirroring real session start.
        var session = InstallSession.Create(new[] { "/silent", $"/LOG={logPath}" });
        session.ResolveSessionLanguage();

        // Simulate the Task-13-style bug: something reads .Current after a reset,
        // i.e. before resolution has (yet again) happened.
        SessionLanguage.ResetForTesting();

#if DEBUG
        // Debug throws before the hook ever runs (by design — see SessionLanguage.Current).
        var act = () => _ = SessionLanguage.Current;
        act.Should().Throw<InvalidOperationException>();
#else
        SessionLanguage.Current.Should().Be(Lang.En);
        File.ReadAllText(logPath).Should().Contain("SessionLanguage.Current read before resolution");
#endif
    }
}
