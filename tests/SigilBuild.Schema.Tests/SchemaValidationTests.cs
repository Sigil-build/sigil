using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using NJsonSchema;
using Xunit;
using YamlDotNet.RepresentationModel;

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
        var stream = new YamlStream();
        stream.Load(new StringReader(yamlText));
        if (stream.Documents.Count == 0)
        {
            return "null";
        }

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            WriteNode(writer, stream.Documents[0].RootNode);
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteNode(Utf8JsonWriter writer, YamlNode node)
    {
        switch (node)
        {
            case YamlMappingNode map:
                writer.WriteStartObject();
                foreach (var kv in map.Children)
                {
                    writer.WritePropertyName(((YamlScalarNode)kv.Key).Value ?? string.Empty);
                    WriteNode(writer, kv.Value);
                }
                writer.WriteEndObject();
                break;
            case YamlSequenceNode seq:
                writer.WriteStartArray();
                foreach (var child in seq.Children)
                {
                    WriteNode(writer, child);
                }
                writer.WriteEndArray();
                break;
            case YamlScalarNode scalar:
                WriteScalar(writer, scalar);
                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }

    private static void WriteScalar(Utf8JsonWriter writer, YamlScalarNode scalar)
    {
        var value = scalar.Value;
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        // Quoted strings keep their string type
        if (scalar.Style is YamlDotNet.Core.ScalarStyle.DoubleQuoted or YamlDotNet.Core.ScalarStyle.SingleQuoted)
        {
            writer.WriteStringValue(value);
            return;
        }

        if (value.Length == 0 || value == "~" || value.Equals("null", System.StringComparison.OrdinalIgnoreCase))
        {
            writer.WriteNullValue();
            return;
        }

        if (bool.TryParse(value, out var b))
        {
            writer.WriteBooleanValue(b);
            return;
        }

        if (long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var i))
        {
            writer.WriteNumberValue(i);
            return;
        }

        if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
        {
            writer.WriteNumberValue(d);
            return;
        }

        writer.WriteStringValue(value);
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

    [Fact]
    public async Task ReferenceInstallerManifest_IsValidAgainstSchema()
    {
        var schema = await LoadSchemaAsync();
        var json = YamlToJson(await File.ReadAllTextAsync("Fixtures/valid/reference-installer-manifest.yaml"));
        var errors = schema.Validate(json);

        errors.Should().BeEmpty(
            "the IMPLEMENTATION_SPEC section 3 reference manifest must satisfy the updated installer schema; got: {0}",
            string.Join("; ", errors.Select(e => e.ToString())));
    }

    [Fact]
    public async Task ScheduledTaskCreateFixture_IsValidAgainstSchema()
    {
        // T11.1 (P11): scheduled_task_create must be accepted by the step-type
        // enum in every place it's duplicated (install_steps / pre_install /
        // post_install / uninstall / hooks).
        var schema = await LoadSchemaAsync();
        var json = YamlToJson(await File.ReadAllTextAsync("Fixtures/valid/scheduled-task-create.yaml"));
        var errors = schema.Validate(json);

        errors.Should().BeEmpty(
            "the scheduled_task_create fixture must satisfy the schema; got: {0}",
            string.Join("; ", errors.Select(e => e.ToString())));
    }

    [Fact]
    public async Task AllowOutsideInstallDirFixture_IsValidAgainstSchema()
    {
        // R16 (S2.4): the `allow_outside_install_dir` opt-out is declared on the
        // step shape, which the schema duplicates across install_steps /
        // pre_install / post_install / uninstall and the InstallStep /
        // InstallStepList definitions. The fixture uses the key in each of those
        // positions, on every step type that accepts it, plus the omitted and the
        // explicit-false cases — so a declaration missed in one copy fails here
        // rather than at pack time in a publisher's manifest.
        var schema = await LoadSchemaAsync();
        var json = YamlToJson(await File.ReadAllTextAsync("Fixtures/valid/allow-outside-install-dir.yaml"));
        var errors = schema.Validate(json);

        errors.Should().BeEmpty(
            "the allow_outside_install_dir fixture must satisfy the schema; got: {0}",
            string.Join("; ", errors.Select(e => e.ToString())));
    }

    [Fact]
    public async Task AllowOutsideInstallDir_RejectsANonBooleanValue()
    {
        // The property is typed, so a plausible-looking string is caught. The step
        // shape carries `additionalProperties: true`, which means an UNDECLARED
        // key would be accepted silently — declaring it is exactly what buys this.
        var schema = await LoadSchemaAsync();
        var yaml = await File.ReadAllTextAsync("Fixtures/valid/allow-outside-install-dir.yaml");
        var json = YamlToJson(yaml.Replace(
            "allow_outside_install_dir: true",
            "allow_outside_install_dir: \"yes-please\"",
            System.StringComparison.Ordinal));

        schema.Validate(json).Should().NotBeEmpty(
            "allow_outside_install_dir is declared as a boolean, so a string must be rejected");
    }

    [Fact]
    public async Task ComRegisterFixture_IsValidAgainstSchema()
    {
        // T11.2 (P11): com_register must be accepted by the step-type enum in
        // every place it's duplicated (install_steps / pre_install /
        // post_install / uninstall / hooks / option components).
        var schema = await LoadSchemaAsync();
        var json = YamlToJson(await File.ReadAllTextAsync("Fixtures/valid/com-register.yaml"));
        var errors = schema.Validate(json);

        errors.Should().BeEmpty(
            "the com_register fixture must satisfy the schema; got: {0}",
            string.Join("; ", errors.Select(e => e.ToString())));
    }

    [Fact]
    public async Task FirewallRuleFixture_IsValidAgainstSchema()
    {
        // T11.3 (P11): firewall_rule must be accepted by the step-type enum in
        // every place it's duplicated (install_steps / pre_install /
        // post_install / uninstall / hooks / option components).
        var schema = await LoadSchemaAsync();
        var json = YamlToJson(await File.ReadAllTextAsync("Fixtures/valid/firewall-rule.yaml"));
        var errors = schema.Validate(json);

        errors.Should().BeEmpty(
            "the firewall_rule fixture must satisfy the schema; got: {0}",
            string.Join("; ", errors.Select(e => e.ToString())));
    }

    public static IEnumerable<object[]> InvalidFixturePaths() =>
        Directory.EnumerateFiles("Fixtures/invalid", "*.yaml").Select(p => new object[] { p });

    [Theory]
    [MemberData(nameof(InvalidFixturePaths))]
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
