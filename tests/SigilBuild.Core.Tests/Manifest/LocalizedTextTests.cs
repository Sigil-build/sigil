using System.Collections.Generic;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using Xunit;

namespace SigilBuild.Core.Tests.Manifest;

public class LocalizedTextTests
{
    [Fact]
    public void Plain_NormalizesToEnglishKeyedMap()
    {
        var text = LocalizedText.Plain("Configure");

        text.Values.Should().HaveCount(1);
        text.Values["en"].Should().Be("Configure");
    }

    [Fact]
    public void Map_IsCarriedVerbatim()
    {
        var text = new LocalizedText(new Dictionary<string, string> { ["en"] = "Configure", ["uk"] = "Налаштування" });

        text.Values.Should().HaveCount(2);
        text.Values["uk"].Should().Be("Налаштування");
    }

    [Fact]
    public void HasEnglish_IsFalse_WhenMapOmitsIt()
    {
        var text = new LocalizedText(new Dictionary<string, string> { ["uk"] = "Налаштування" });

        text.HasEnglish.Should().BeFalse("SIG0290 keys off this");
    }

    [Fact]
    public void HasEnglish_IsCaseInsensitive()
    {
        new LocalizedText(new Dictionary<string, string> { ["EN"] = "x" }).HasEnglish.Should().BeTrue();
    }
}
