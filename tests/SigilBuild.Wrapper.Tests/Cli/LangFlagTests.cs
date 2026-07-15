using System;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Cli;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Cli;

public class LangFlagTests
{
    [Fact]
    public void Lang_ParsesTag()
    {
        Parse("/lang=uk").Lang.Should().Be("uk");
    }

    [Fact]
    public void Lang_AcceptsRegionSubtag()
    {
        Parse("/lang=pt-BR").Lang.Should().Be("pt-BR");
    }

    [Fact]
    public void Lang_WellFormedButUnknown_IsAccepted_NotAnError()
    {
        // Sigil ships no `de` chrome, but a manifest may supply `de` screens.
        // Rejecting this would break design §4.4.
        Parse("/lang=de").Lang.Should().Be("de");
    }

    [Fact]
    public void Lang_Empty_IsUsageError()
    {
        var act = () => Parse("/lang=");
        act.Should().Throw<UsageException>().WithMessage("*requires a language tag*");
    }

    [Fact]
    public void Lang_Malformed_IsUsageError()
    {
        var act = () => Parse("/lang=!!");
        act.Should().Throw<UsageException>();
    }

    [Fact]
    public void Lang_Pseudo_IsUsageError_SoPseudoIsUnreachable()
    {
        var act = () => Parse("/lang=pseudo");
        act.Should().Throw<UsageException>("'pseudo' is 6 alpha chars; the grammar allows 2-3");
    }

    [Fact]
    public void Launch_StillParses_NoCollisionWithLang()
    {
        Parse("/launch").Launch.Should().BeTrue();
    }

    [Fact]
    public void AuditSafeRendering_IncludesLang()
    {
        Parse("/lang=uk").AuditSafeRendering().Should().Contain("/lang=uk");
    }

    // Design §6.2: SIG0291 and /lang are the same rule. This pins the two call
    // sites to one implementation — if someone re-implements either side, the
    // shared truth table below diverges and this fails.
    [Theory]
    [InlineData("uk", true)]
    [InlineData("pt-BR", true)]
    [InlineData("de", true)]
    [InlineData("!!", false)]
    [InlineData("pseudo", false)]
    [InlineData("e", false)]
    public void LangFlag_AcceptsExactlyWhatLanguageTagAccepts(string tag, bool valid)
    {
        LanguageTag.IsValid(tag).Should().Be(valid, "the validator is the shared source of truth");

        var act = () => Parse($"/lang={tag}");
        if (valid)
        {
            act.Should().NotThrow();
        }
        else
        {
            act.Should().Throw<UsageException>();
        }
    }

    [Fact]
    public void Help_ListsLang_AndDoesNotImplyManifestLanguagesAreLimited()
    {
        var help = HelpText.Render();
        help.Should().Contain("/lang=");
        help.Should().Contain("chrome ships in: en, uk");
        help.Should().Contain("manifest screens may supply any tag");
    }

    private static ParsedCommandLine Parse(params string[] args) =>
        CommandLineParser.Parse(args, Array.Empty<ParameterDefinition>());
}
