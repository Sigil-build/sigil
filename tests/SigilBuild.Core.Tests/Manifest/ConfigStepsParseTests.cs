using System.Linq;
using FluentAssertions;
using SigilBuild.Core.Configuration;
using SigilBuild.Core.Diagnostics;
using SigilBuild.Core.Manifest;
using Xunit;

namespace SigilBuild.Core.Tests.Manifest;

/// <summary>
/// P8: parsing of the ini_write / json_edit / xml_edit steps, including the
/// create_if_missing default (false) and required-field diagnostics.
/// </summary>
public class ConfigStepsParseTests
{
    private static string Yaml(string step) =>
        "spec: v1.0\n" +
        "app: { id: com.example.App, name: App, version: 0.1.0, publisher: P }\n" +
        "build: { source: ./out }\n" +
        "install_steps:\n  - " + step + "\n";

    [Fact]
    public void Parses_all_three_config_steps_defaulting_create_if_missing_false()
    {
        var yaml =
            "spec: v1.0\n" +
            "app: { id: com.example.App, name: App, version: 0.1.0, publisher: P }\n" +
            "build: { source: ./out }\n" +
            "install_steps:\n" +
            "  - { id: i, type: ini_write, path: a.ini, section: app, key: k, value: \"{var.v}\" }\n" +
            "  - { id: j, type: json_edit, path: a.json, pointer: /a/b, value: \"1\", create_if_missing: true }\n" +
            "  - { id: x, type: xml_edit, path: a.xml, xpath: /root/a, attribute: id, value: v }\n";

        var result = ManifestParser.Parse(yaml, "s.yaml");
        result.Diagnostics.Should().NotContain(d => d.Severity == DiagnosticSeverity.Error);

        var steps = result.Manifest!.InstallSteps!;
        var ini = steps.OfType<InstallStep.IniWrite>().Single();
        ini.Section.Should().Be("app");
        ini.Key.Should().Be("k");
        ini.Value.Should().Be("{var.v}");
        ini.CreateIfMissing.Should().BeFalse("create_if_missing defaults to false");

        var json = steps.OfType<InstallStep.JsonEdit>().Single();
        json.JsonPointer.Should().Be("/a/b");
        json.CreateIfMissing.Should().BeTrue();

        var xml = steps.OfType<InstallStep.XmlEdit>().Single();
        xml.Xpath.Should().Be("/root/a");
        xml.Attribute.Should().Be("id");
    }

    [Theory]
    [InlineData("{ id: i, type: ini_write, path: a.ini, section: app }")]      // no key
    [InlineData("{ id: j, type: json_edit, path: a.json }")]                    // no pointer
    [InlineData("{ id: x, type: xml_edit, path: a.xml }")]                      // no xpath
    public void Missing_required_field_diagnoses(string step)
        => ManifestParser.Parse(Yaml(step), "s.yaml")
            .Diagnostics.Should().Contain(d => d.Code == DiagnosticCodes.MissingRequiredStepField);
}
