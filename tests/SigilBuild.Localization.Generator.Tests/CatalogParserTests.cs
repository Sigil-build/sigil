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
}
