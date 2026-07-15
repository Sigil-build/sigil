using FluentAssertions;
using SigilBuild.Localization.Generator;
using Xunit;

namespace SigilBuild.Localization.Generator.Tests;

public class StringsEmitterTests
{
    private static string EmitFor(string enText, string ukText)
    {
        var files = new[]
        {
            CatalogParser.Parse("Strings.en.txt", enText),
            CatalogParser.Parse("Strings.uk.txt", ukText),
        };
        return StringsEmitter.Emit(files);
    }

    [Fact]
    public void Emit_ArglessKey_ProducesSwitchOverLang()
    {
        var src = EmitFor("nav.back = Back\n", "nav.back = Назад\n");

        src.Should().Contain("public static string NavBack(Lang lang)");
        src.Should().Contain("Lang.Uk => \"Назад\"");
        src.Should().Contain("_ => \"Back\"");
    }

    [Fact]
    public void Emit_NamedPlaceholders_BecomeTypedParametersAndConcatenation()
    {
        var src = EmitFor(
            "upgrading = Upgrading {appName} to {toVersion}.\n",
            "upgrading = Оновлення {appName} до {toVersion}.\n");

        src.Should().Contain("public static string Upgrading(Lang lang, string appName, string toVersion)");
        src.Should().Contain("\"Upgrading \" + appName + \" to \" + toVersion + \".\"");
        // No string.Format anywhere: formatting must never touch CultureInfo.
        src.Should().NotContain("string.Format");
    }

    [Fact]
    public void Emit_TranslationMayReorderPlaceholders()
    {
        // Word order differs; the uk expression must reflect its own order.
        var src = EmitFor(
            "greet = Hello {first} {last}\n",
            "greet = Вітаю {last} {first}\n");

        src.Should().Contain("\"Вітаю \" + last + \" \" + first");
    }

    [Fact]
    public void Emit_KeyMissingFromTranslation_FallsBackToEnglish()
    {
        var src = EmitFor("nav.back = Back\nnav.next = Next\n", "nav.back = Назад\n");

        src.Should().Contain("public static string NavNext(Lang lang)");
        // NavNext has no Lang.Uk arm — it lands on the `_ =>` English default.
        src.Should().NotContain("Lang.Uk => \"Next\"");
    }

    // ChromeCatalog is emitted from the catalog files so LanguageResolver never
    // hardcodes a language list (ADR-008 §4: languages ship as content).
    [Fact]
    public void Emit_ChromeCatalog_ListsEveryCatalogTag()
    {
        var src = EmitFor("nav.back = Back\n", "nav.back = Назад\n");

        src.Should().Contain("public static readonly string[] Tags = { \"en\", \"uk\" };");
        src.Should().Contain("\"uk\" => Lang.Uk,");
    }

    // The regression this design prevents: a third catalog wires itself with no
    // code edit. If this fails, adding a language silently half-works.
    [Fact]
    public void Emit_ThirdLanguage_AppearsInChromeCatalog_WithNoCodeChange()
    {
        var files = new[]
        {
            CatalogParser.Parse("Strings.en.txt", "nav.back = Back\n"),
            CatalogParser.Parse("Strings.uk.txt", "nav.back = Назад\n"),
            CatalogParser.Parse("Strings.de.txt", "nav.back = Zurück\n"),
        };

        var src = StringsEmitter.Emit(files);

        src.Should().Contain("public enum Lang { En, De, Uk, Pseudo }");
        src.Should().Contain("public static readonly string[] Tags = { \"en\", \"de\", \"uk\" };");
        src.Should().Contain("\"de\" => Lang.De,");
    }
}
