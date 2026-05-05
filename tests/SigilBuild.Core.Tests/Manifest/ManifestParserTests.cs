using FluentAssertions;
using SigilBuild.Core.Configuration;
using SigilBuild.Core.Diagnostics;
using SigilBuild.Core.Manifest;
using Xunit;

namespace SigilBuild.Core.Tests.Manifest;

public class ManifestParserTests
{
    [Fact]
    public void Parse_MinimalDocument_ReturnsManifestWithSourceLocations()
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

        var result = ManifestParser.Parse(yaml, "sigil.yaml");

        result.Diagnostics.Should().BeEmpty();
        result.Manifest.Should().NotBeNull();
        result.Manifest!.Spec.Should().Be("v1.0");
        result.Manifest.App.Id.Should().Be("com.example.App");
        result.Manifest.Build.Source.Should().Be("./out");
        result.Manifest.Build.Deterministic.Should().BeTrue();
        result.Manifest.Location.File.Should().Be("sigil.yaml");
        result.Manifest.Location.Line.Should().Be(1);
    }

    [Fact]
    public void Parse_SyntaxError_ReportsLineAndColumn()
    {
        const string yaml = "spec: v1.0\napp:\n  id: : :\n";

        var result = ManifestParser.Parse(yaml, "broken.yaml");

        result.Manifest.Should().BeNull();
        result.Diagnostics.Should().ContainSingle()
            .Which.Code.Should().Be(DiagnosticCodes.YamlSyntaxError);
        result.Diagnostics[0].Location.Line.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Parse_PackageDefaults_ApplyZipAndX64()
    {
        const string yaml = """
            spec: v1.0
            app: { id: com.example.App, name: Example, version: 0.1.0, publisher: Example Inc. }
            build: { source: ./out }
            package: {}
            """;

        var result = ManifestParser.Parse(yaml, "sigil.yaml");

        result.Diagnostics.Should().BeEmpty();
        result.Manifest!.Package!.Formats.Should().ContainSingle().Which.Should().Be(PackageFormat.Zip);
        result.Manifest.Package.Architectures.Should().ContainSingle().Which.Should().Be(TargetArchitecture.X64);
    }
}
