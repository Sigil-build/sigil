using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using NJsonSchema;
using YamlDotNet.Serialization;
using Xunit;

namespace SigilBuild.Schema.Tests;

public class SchemaValidationTests
{
    private const string SchemaFile = "sigil-schema.json";

    private static async Task<JsonSchema> LoadSchemaAsync()
    {
        var schemaJson = await File.ReadAllTextAsync(SchemaFile);
        return await JsonSchema.FromJsonAsync(schemaJson);
    }

    private static string YamlToJson(string yamlText)
    {
        var deserializer = new DeserializerBuilder().Build();
        var yamlObject = deserializer.Deserialize(new StringReader(yamlText));
        var serializer = new SerializerBuilder()
            .JsonCompatible()
            .Build();
        return serializer.Serialize(yamlObject ?? new object());
    }

    [Theory]
    [InlineData("examples/minimal/sigil.yaml")]
    [InlineData("examples/msix-local-sign/sigil.yaml")]
    [InlineData("examples/azure-trusted-signing/sigil.yaml")]
    [InlineData("examples/full/sigil.yaml")]
    public async Task Examples_AreValidAgainstSchema(string yamlPath)
    {
        var schema = await LoadSchemaAsync();
        var json = YamlToJson(await File.ReadAllTextAsync(yamlPath));
        var errors = schema.Validate(json);

        errors.Should().BeEmpty(
            "{0} must satisfy the v1.0 schema; got: {1}",
            yamlPath,
            string.Join("; ", errors.Select(e => e.ToString())));
    }

    [Theory]
    [InlineData("Fixtures/invalid/missing-app.yaml")]
    [InlineData("Fixtures/invalid/bad-version.yaml")]
    public async Task InvalidFixtures_AreRejected(string yamlPath)
    {
        var schema = await LoadSchemaAsync();
        var json = YamlToJson(await File.ReadAllTextAsync(yamlPath));
        var errors = schema.Validate(json);

        errors.Should().NotBeEmpty("{0} is intentionally invalid", yamlPath);
    }

    [Fact]
    public async Task Schema_PinsSpecVersionToV1_0()
    {
        var schema = await LoadSchemaAsync();
        var specProperty = schema.Properties["spec"];
        specProperty.Should().NotBeNull();
        specProperty!.ExtensionData.Should().ContainKey("const");
        specProperty.ExtensionData!["const"].Should().Be("v1.0");
    }
}
