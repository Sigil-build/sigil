using System;
using System.Collections.Generic;
using FluentAssertions;
using SigilBuild.Wrapper.Core.Localization;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Localization;

public class LanguageResolverTests
{
    private static readonly string[] Os = { "de-DE", "uk-UA" };

    [Fact]
    public void ManifestLanguage_Wins_OverFlagAndOs()
    {
        LanguageResolver.Preferences("en", "uk", Os).Should().Equal("en");
    }

    [Fact]
    public void Flag_Wins_OverOs()
    {
        LanguageResolver.Preferences(null, "uk", Os).Should().Equal("uk");
    }

    [Fact]
    public void Os_UsedWhenNoManifestOrFlag_AsFullOrderedList()
    {
        LanguageResolver.Preferences(null, null, Os).Should().Equal("de-DE", "uk-UA");
    }

    [Fact]
    public void NothingAvailable_FallsBackToEn()
    {
        LanguageResolver.Preferences(null, null, Array.Empty<string>()).Should().Equal("en");
    }

    [Fact]
    public void Match_ExactBeatsEverything_CaseInsensitive()
    {
        LanguageResolver.Match(new[] { "pt-br" }, new[] { "en", "pt-BR" }).Should().Be("pt-BR");
    }

    [Fact]
    public void Match_FallsBackToPrimarySubtag()
    {
        LanguageResolver.Match(new[] { "de-AT" }, new[] { "en", "de" }).Should().Be("de");
    }

    [Fact]
    public void Match_PrimarySubtagPicksOrdinalFirst_Deterministically()
    {
        LanguageResolver.Match(new[] { "de" }, new[] { "de-CH", "de-AT", "en" }).Should().Be("de-AT");
    }

    [Fact]
    public void Match_NoHit_FallsBackToEn()
    {
        LanguageResolver.Match(new[] { "zz" }, new[] { "en", "uk" }).Should().Be("en");
    }

    // The reason list-walk exists (design §4.2). This test fails under first-only.
    [Fact]
    public void Match_WalksPastUnavailableTopPreference()
    {
        LanguageResolver.Match(Os, new[] { "en", "uk" }).Should().Be("uk");
    }

    [Fact]
    public void MatchChrome_ForThatSameList_IsUk_NotEn()
    {
        LanguageResolver.MatchChrome(Os).Should().Be(Lang.Uk);
    }

    [Fact]
    public void MatchChrome_NeverReturnsPseudo()
    {
        LanguageResolver.MatchChrome(new[] { "pseudo" }).Should().Be(Lang.En);
        LanguageResolver.MatchChrome(new[] { "qps" }).Should().Be(Lang.En);
    }

    [Fact]
    public void InvalidManifestLanguage_IsIgnored_NotCrashed()
    {
        // SIG0291 rejects it at pack time; a blob that predates the check must not crash.
        LanguageResolver.Preferences("!!", null, Os).Should().Equal("de-DE", "uk-UA");
    }
}
