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

    [Fact]
    public void Emit_PlaceholderAtStartOfValue_ProducesLeadingConcatenation()
    {
        var src = EmitFor("greet = {name} hello\n", "greet = {name} привіт\n");

        src.Should().Contain("public static string Greet(Lang lang, string name)");
        src.Should().Contain("_ => name + \" hello\",");
    }

    [Fact]
    public void Emit_PlaceholderAtEndOfValue_ProducesTrailingConcatenation()
    {
        var src = EmitFor("greet = hello {name}\n", "greet = привіт {name}\n");

        src.Should().Contain("_ => \"hello \" + name,");
    }

    [Fact]
    public void Emit_AdjacentPlaceholders_ConcatenateWithNoLiteralBetween()
    {
        var src = EmitFor("x = {a}{b}\n", "x = {b}{a}\n");

        src.Should().Contain("public static string X(Lang lang, string a, string b)");
        src.Should().Contain("_ => a + b,");
        src.Should().Contain("Lang.Uk => b + a,");
    }

    [Fact]
    public void Emit_UnclosedBrace_IsEmittedAsLiteralText()
    {
        var src = EmitFor("x = a { b\n", "x = a { b\n");

        src.Should().Contain("public static string X(Lang lang)");
        src.Should().Contain("_ => \"a { b\",");
    }

    [Fact]
    public void Emit_EmptyOrNumericBraces_AreNotPlaceholders_EmittedAsLiteral()
    {
        var src = EmitFor("x = {} and {0}\n", "x = {} and {0}\n");

        src.Should().Contain("public static string X(Lang lang)");
        src.Should().Contain("_ => \"{} and {0}\",");
    }

    [Fact]
    public void Emit_NonIdentifierBraceSpans_AreLiteralNotIdentifiers()
    {
        // {_x} (leading underscore) and {foo.bar} (dotted) are NOT placeholder names
        // per CatalogParser.PlaceholderPattern, so they must be emitted as literal text —
        // not as bare identifiers with no matching declared parameter (CS0103).
        var src = EmitFor("x = {_x} and {foo.bar}\n", "x = {_x} and {foo.bar}\n");

        src.Should().Contain("public static string X(Lang lang)");
        src.Should().Contain("_ => \"{_x} and {foo.bar}\",");
    }

    [Fact]
    public void Quote_EscapesBackslashAndDoubleQuote()
    {
        var src = EmitFor("path = C:\\Temp\\ and a \"quote\"\n", "path = C:\\Temp\\ and a \"quote\"\n");

        src.Should().Contain("\"C:\\\\Temp\\\\ and a \\\"quote\\\"\"");
    }
}
