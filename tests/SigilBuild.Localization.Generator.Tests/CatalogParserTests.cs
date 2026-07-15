using FluentAssertions;
using SigilBuild.Localization.Generator;
using Xunit;

namespace SigilBuild.Localization.Generator.Tests;

public class CatalogParserTests
{
    [Fact]
    public void Parse_ReadsKeyValuePairs_AndSkipsCommentsAndBlanks()
    {
        var text = "# provenance header\n\nnav.back = Back\nnav.next = Next\n";

        var file = CatalogParser.Parse("Strings.en.txt", text);

        file.Lang.Should().Be("en");
        file.Entries.Should().HaveCount(2);
        file.Entries[0].Key.Should().Be("nav.back");
        file.Entries[0].Value.Should().Be("Back");
        file.Entries[1].Key.Should().Be("nav.next");
    }

    [Fact]
    public void Parse_ExtractsNamedPlaceholders_InOrder()
    {
        var text = "upgrading = Upgrading {appName} from {fromVersion} to {toVersion}.\n";

        var file = CatalogParser.Parse("Strings.en.txt", text);

        file.Entries[0].Placeholders.Should().Equal("appName", "fromVersion", "toVersion");
    }

    [Fact]
    public void Parse_RecordsMalformed_ForLineWithNoEquals()
    {
        var text = "nav.back = Back\nthis line has no equals sign\n";

        var file = CatalogParser.Parse("Strings.en.txt", text);

        file.Entries.Should().ContainSingle();
        file.Malformed.Should().ContainSingle();
        file.Malformed[0].Line.Should().Be(2);
        file.Malformed[0].Message.Should().Contain("expected 'key = value'");
    }

    [Fact]
    public void Parse_RecordsMalformed_ForLineStartingWithEquals()
    {
        // Input " = value" is trimmed to "= value", so eq == 0: there IS an '=', just with
        // an empty key to its left. Task 3 narrowed the guard from `eq <= 0` to `eq < 0`,
        // which separates "no '=' at all" from "empty key" and makes this branch reachable,
        // reporting the more precise "key is empty" instead of the generic parse message.
        var text = "nav.back = Back\n = value\n";

        var file = CatalogParser.Parse("Strings.en.txt", text);

        file.Entries.Should().ContainSingle();
        file.Malformed.Should().ContainSingle();
        file.Malformed[0].Line.Should().Be(2);
        file.Malformed[0].Message.Should().Contain("key is empty");
    }

    [Fact]
    public void Parse_NormalizesCrlfLineEndings()
    {
        var text = "a = A\r\nb = B\r\n";

        var file = CatalogParser.Parse("Strings.en.txt", text);

        file.Entries.Should().HaveCount(2);
        file.Entries[0].Key.Should().Be("a");
        file.Entries[0].Value.Should().Be("A");
        file.Entries[0].Line.Should().Be(1);
        file.Entries[1].Key.Should().Be("b");
        file.Entries[1].Value.Should().Be("B");
        file.Entries[1].Line.Should().Be(2);
    }

    [Fact]
    public void Parse_TrimsWhitespace_AroundKeyAndValue()
    {
        var text = "  nav.back   =   Back  \n";

        var file = CatalogParser.Parse("Strings.en.txt", text);

        file.Entries[0].Key.Should().Be("nav.back");
        file.Entries[0].Value.Should().Be("Back");
    }

    [Fact]
    public void Parse_SplitsAtFirstEquals_KeepingRemainderInValue()
    {
        var text = "eq = a = b\n";

        var file = CatalogParser.Parse("Strings.en.txt", text);

        file.Entries[0].Key.Should().Be("eq");
        file.Entries[0].Value.Should().Be("a = b");
    }

    [Fact]
    public void Parse_DoesNotTreatPositionalOrEmptyBraces_AsPlaceholders()
    {
        var text = "x = {0} and {}\n";

        var file = CatalogParser.Parse("Strings.en.txt", text);

        file.Entries[0].Placeholders.Should().BeEmpty();
    }
}
