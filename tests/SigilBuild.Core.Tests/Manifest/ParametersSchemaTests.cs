using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Configuration;
using SigilBuild.Core.Diagnostics;
using SigilBuild.Core.Manifest;
using Xunit;

namespace SigilBuild.Core.Tests.Manifest;

public class ParametersSchemaTests
{
    private const string ValidPrelude = """
        spec: v1.0
        app:
          id: com.example.App
          name: Example
          version: 0.1.0
          publisher: Example Inc.
        build:
          source: ./out
        """;

    [Theory]
    [InlineData("string")]
    [InlineData("path")]
    [InlineData("bool")]
    [InlineData("int")]
    [InlineData("enum")]
    [InlineData("secret")]
    public async Task Valid_parameter_type_passes_validation(string type)
    {
        var yaml = $$"""
            {{ValidPrelude}}
            parameters:
              p:
                type: {{type}}
                install_time: true
            """;
        var diagnostics = await ManifestLoader.ValidateAsync(yaml);
        diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
    }

    [Fact]
    public async Task Unknown_parameter_type_is_rejected_with_SIG02_code()
    {
        var yaml = $$"""
            {{ValidPrelude}}
            parameters:
              p:
                type: object
            """;
        var diagnostics = await ManifestLoader.ValidateAsync(yaml);
        diagnostics.Should().Contain(d => d.Code.StartsWith("SIG02"));
    }

    [Fact]
    public async Task Secret_parameter_value_is_redacted_in_diagnostics()
    {
        var yaml = $$"""
            {{ValidPrelude}}
            parameters:
              license:
                type: secret
                install_time: true
                pattern: "^[A-Z]{4}-[0-9]{4}$"
            """;
        var diagnostics = await ManifestLoader.ValidateWithSampleValuesAsync(
            yaml,
            new Dictionary<string, string> { ["license"] = "wrong-format-value" });
        diagnostics.Should().Contain(d =>
            d.Message.Contains("license") && !d.Message.Contains("wrong-format-value"));
    }

    [Fact]
    public async Task Manifest_without_parameters_block_keeps_Parameters_null()
    {
        var yaml = ValidPrelude;
        var diagnostics = await ManifestLoader.ValidateAsync(yaml);
        diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();

        var parsed = ManifestParser.Parse(yaml, "<inline>");
        parsed.Manifest!.Parameters.Should().BeNull();
    }

    [Fact]
    public async Task Sample_value_matching_pattern_does_not_emit_failure()
    {
        var yaml = $$"""
            {{ValidPrelude}}
            parameters:
              license:
                type: string
                install_time: true
                pattern: "^[A-Z]{4}-[0-9]{4}$"
            """;
        var diagnostics = await ManifestLoader.ValidateWithSampleValuesAsync(
            yaml,
            new Dictionary<string, string> { ["license"] = "ABCD-1234" });
        diagnostics.Where(d => d.Code == DiagnosticCodes.ParameterValidationFailure)
            .Should().BeEmpty();
    }

    // ── LocalizedText (P9, gap G10): parameter `description` normalizes/carries
    //    {en, uk, ...} the same way installer.screens title/subtitle do. ──────

    [Fact]
    public void PlainStringDescription_NormalizesToEnglish()
    {
        var yaml = $$"""
            {{ValidPrelude}}
            parameters:
              p:
                type: string
                description: "Server address"
            """;

        var result = ManifestParser.Parse(yaml, "<inline>");

        result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
        result.Manifest!.Parameters!["p"].Description!.Values["en"].Should().Be("Server address");
    }

    [Fact]
    public void MapDescription_WithoutEnglish_EmitsSig0290_AsError()
    {
        var yaml = $$"""
            {{ValidPrelude}}
            parameters:
              p:
                type: string
                description:
                  uk: Адреса сервера
            """;

        var result = ManifestParser.Parse(yaml, "<inline>");

        result.Diagnostics.Should().Contain(d =>
            d.Code == DiagnosticCodes.LocalizedTextMissingEnglish && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void MapDescription_WithEnglish_IsAccepted()
    {
        var yaml = $$"""
            {{ValidPrelude}}
            parameters:
              p:
                type: string
                description:
                  en: Server address
                  uk: Адреса сервера
            """;

        var result = ManifestParser.Parse(yaml, "<inline>");

        result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
        result.Manifest!.Parameters!["p"].Description!.Values.Should().HaveCount(2);
    }
}
