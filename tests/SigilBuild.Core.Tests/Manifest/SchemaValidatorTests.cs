using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Configuration;
using SigilBuild.Core.Diagnostics;
using Xunit;

namespace SigilBuild.Core.Tests.Manifest;

public class SchemaValidatorTests
{
    [Fact]
    public async Task Validate_MinimalManifest_ReturnsNoDiagnostics()
    {
        const string yaml = """
            spec: v1.0
            app:
              id: com.example.App
              name: Example
              version: 0.1.0
              publisher: Example Inc.
            build:
              source: ./out
            """;

        var diags = await SchemaValidator.ValidateAsync(yaml, "sigil.yaml");

        diags.Should().BeEmpty();
    }

    [Fact]
    public async Task Validate_BadSpec_ReportsSchemaViolation()
    {
        const string yaml = """
            spec: v999
            app: { id: com.example.App, name: Example, version: 0.1.0, publisher: Example Inc. }
            build: { source: ./out }
            """;

        var diags = await SchemaValidator.ValidateAsync(yaml, "sigil.yaml");

        diags.Should().NotBeEmpty();
        diags.Should().AllSatisfy(d => d.Code.Should().Be(DiagnosticCodes.SchemaViolation));
    }

    [Fact]
    public async Task Validate_BadVersion_ReportsSchemaViolation()
    {
        const string yaml = """
            spec: v1.0
            app: { id: com.example.App, name: Example, version: not-a-semver, publisher: Example Inc. }
            build: { source: ./out }
            """;

        var diags = await SchemaValidator.ValidateAsync(yaml, "sigil.yaml");

        diags.Should().NotBeEmpty();
        diags.Should().AllSatisfy(d => d.Code.Should().Be(DiagnosticCodes.SchemaViolation));
    }
}
