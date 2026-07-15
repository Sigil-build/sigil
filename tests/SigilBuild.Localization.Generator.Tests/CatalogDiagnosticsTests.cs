using System.Linq;
using FluentAssertions;
using SigilBuild.Localization.Generator;
using Xunit;

namespace SigilBuild.Localization.Generator.Tests;

public class CatalogDiagnosticsTests
{
    private static string[] Validate(string en, string uk) =>
        CatalogValidator.Validate(new[]
        {
            CatalogParser.Parse("Strings.en.txt", en),
            CatalogParser.Parse("Strings.uk.txt", uk),
        }).Select(p => p.Id).ToArray();

    [Fact]
    public void OrphanKeyInTranslation_IsError001()
    {
        Validate("a = A\n", "a = А\nb = Б\n").Should().Contain("SIGLOC001");
    }

    [Fact]
    public void KeyMissingFromTranslation_IsWarning002()
    {
        Validate("a = A\nb = B\n", "a = А\n").Should().Contain("SIGLOC002");
    }

    [Fact]
    public void DroppedPlaceholder_IsError003()
    {
        // The translator lost {toVersion} — the exact defect this catches.
        Validate(
            "upgrading = Upgrading {appName} to {toVersion}\n",
            "upgrading = Оновлення {appName}\n").Should().Contain("SIGLOC003");
    }

    [Fact]
    public void ReorderedPlaceholders_AreNotAnError()
    {
        // Set equality, not sequence equality: word order is the translator's business.
        Validate(
            "greet = Hello {first} {last}\n",
            "greet = Вітаю {last} {first}\n").Should().NotContain("SIGLOC003");
    }

    [Fact]
    public void DuplicateKey_IsError004()
    {
        Validate("a = A\na = B\n", "a = А\n").Should().Contain("SIGLOC004");
    }

    [Fact]
    public void MalformedLine_IsError005()
    {
        Validate("this line has no equals sign\n", "a = А\n").Should().Contain("SIGLOC005");
    }

    [Fact]
    public void MatchingCatalogs_ProduceNoProblems()
    {
        Validate("a = A\nb = B {x}\n", "a = А\nb = Б {x}\n").Should().BeEmpty();
    }

    // SIGLOC006 — MethodName splits on '.' AND '_', so these two distinct keys
    // both become LocationErrorNotAbsolute. The emitter would write two
    // identical signatures -> CS0111 pointing at generated source.
    [Fact]
    public void KeysCollidingOnMethodName_IsError006()
    {
        Validate(
            "location.error.notAbsolute = A\nlocation.error.not_absolute = B\n",
            "location.error.notAbsolute = А\nlocation.error.not_absolute = Б\n")
            .Should().Contain("SIGLOC006");
    }

    [Fact]
    public void DistinctMethodNames_AreNotACollision()
    {
        Validate("nav.back = Back\nnav.next = Next\n", "nav.back = Назад\nnav.next = Далі\n")
            .Should().NotContain("SIGLOC006");
    }

    // SIGLOC007 — {class} passes the placeholder regex but emits `string class` -> CS1041.
    [Fact]
    public void PlaceholderNamedForCSharpKeyword_IsError007()
    {
        Validate("x = a {class} b\n", "x = а {class} б\n").Should().Contain("SIGLOC007");
    }

    [Fact]
    public void PlaceholderNamedLikeAKeywordButNotOne_IsFine()
    {
        Validate("x = a {className} b\n", "x = а {className} б\n").Should().NotContain("SIGLOC007");
    }
}
