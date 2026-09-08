using System;
using System.Threading;
using System.Threading.Tasks;
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

    // ── R8: the RUNTIME half of the https rule ────────────────────────────────

    /// <summary>
    /// SIG0323 validates <c>source.url</c> as written in the manifest. This is the URL
    /// actually about to be requested, after token substitution — a URL assembled from
    /// parameter values is not knowable at pack time, so pack-time validation alone
    /// leaves the hole open. The refusal must happen BEFORE the GET, so nothing
    /// cleartext ever reaches the wire.
    /// </summary>
    /// <remarks>
    /// Compiles at the parent commit (it names only <c>LoadAsync</c>) and fails there,
    /// where the loader GETs whatever it is handed.
    /// </remarks>
    [Theory]
    [InlineData("http://example.com/editions.json")]
    [InlineData("ftp://example.com/editions.json")]
    [InlineData("file:///C:/editions.json")]
    public async Task LoadAsync_refuses_a_non_https_url_before_making_the_request(string insecureUrl)
    {
        var act = async () => await HttpOptionsLoader.LoadAsync(
            insecureUrl, itemsPath: "data", labelProperty: "label", valueProperty: "value",
            CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>(
            "a parameter source feeds values that are substituted into install steps executed " +
            "elevated, so it must never be fetched over a cleartext or local-file scheme"))
            .WithMessage("*https*");
    }
}
