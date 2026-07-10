using FluentAssertions;
using SigilBuild.Installer.Host.Services;
using Xunit;

namespace SigilBuild.Installer.Host.Tests;

public class HttpOptionsLoaderTests
{
    [Fact]
    public void ParseJson_ExtractsArray_AtItemsPath_WithLabelAndValueProperties()
    {
        var json = """
            {
              "data": [
                { "applicationId": "uuid-1", "applicationName": "Kiosk-01" },
                { "applicationId": "uuid-2", "applicationName": "Kiosk-02" }
              ]
            }
            """;
        var options = HttpOptionsLoader.ParseJson(json, itemsPath: "data",
            labelProperty: "applicationName", valueProperty: "applicationId");
        options.Should().HaveCount(2);
        options[0].Label.Should().Be("Kiosk-01");
        options[0].Value.Should().Be("uuid-1");
        options[1].Value.Should().Be("uuid-2");
    }

    [Fact]
    public void ParseJson_ReturnsEmptyList_WhenItemsPathMissing()
    {
        var options = HttpOptionsLoader.ParseJson("{}", itemsPath: "data",
            labelProperty: "label", valueProperty: "value");
        options.Should().BeEmpty();
    }

    [Fact]
    public void ParseJson_SkipsItemsWithMissingValueProperty()
    {
        var json = """
            { "data": [ { "label": "A" }, { "value": "b", "label": "B" } ] }
            """;
        var options = HttpOptionsLoader.ParseJson(json, itemsPath: "data",
            labelProperty: "label", valueProperty: "value");
        options.Should().HaveCount(1);
        options[0].Value.Should().Be("b");
    }
}
