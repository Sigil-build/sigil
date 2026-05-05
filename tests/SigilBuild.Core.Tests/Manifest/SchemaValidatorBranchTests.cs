using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Configuration;
using SigilBuild.Core.Diagnostics;
using Xunit;

namespace SigilBuild.Core.Tests.Manifest;

public class SchemaValidatorBranchTests
{
    private const string Header = """
        spec: v1.0
        app: { id: com.example.App, name: Example, version: 0.1.0, publisher: P }
        build: { source: ./out }
        """;

    [Fact]
    public async Task BadHomepageUri_IsRejected()
    {
        const string yaml = """
            spec: v1.0
            app:
              id: com.example.App
              name: Example
              version: 0.1.0
              publisher: P
              homepage: not a uri
            build: { source: ./out }
            """;
        var diags = await SchemaValidator.ValidateAsync(yaml, "x.yaml");
        diags.Should().Contain(d => d.Message.Contains("URI"));
    }

    [Fact]
    public async Task DeltaTargetsOutOfRange_IsRejected()
    {
        const string yaml = $"{Header}\nupdates:\n  deltaTargets: 999\n";
        var diags = await SchemaValidator.ValidateAsync(yaml, "x.yaml");
        diags.Should().Contain(d => d.Message.Contains("maximum"));
    }

    [Fact]
    public async Task DeltaTargetsNegative_IsRejected()
    {
        const string yaml = $"{Header}\nupdates:\n  deltaTargets: -1\n";
        var diags = await SchemaValidator.ValidateAsync(yaml, "x.yaml");
        diags.Should().Contain(d => d.Message.Contains("minimum"));
    }

    [Fact]
    public async Task UnknownEnumArchitecture_IsRejected()
    {
        const string yaml = $"{Header}\npackage:\n  formats: [zip]\n  architectures: [x86]\n";
        var diags = await SchemaValidator.ValidateAsync(yaml, "x.yaml");
        diags.Should().Contain(d => d.Message.Contains("must be one of"));
    }

    [Fact]
    public async Task DuplicateFormats_IsRejected()
    {
        const string yaml = $"{Header}\npackage:\n  formats: [zip, zip]\n";
        var diags = await SchemaValidator.ValidateAsync(yaml, "x.yaml");
        diags.Should().Contain(d => d.Message.Contains("unique"));
    }

    [Fact]
    public async Task EmptyFormats_IsRejected()
    {
        const string yaml = $"{Header}\npackage:\n  formats: []\n";
        var diags = await SchemaValidator.ValidateAsync(yaml, "x.yaml");
        diags.Should().Contain(d => d.Message.Contains("at least 1"));
    }

    [Fact]
    public async Task AdditionalProperty_IsRejected()
    {
        const string yaml = """
            spec: v1.0
            app:
              id: com.example.App
              name: Example
              version: 0.1.0
              publisher: P
              somethingExtra: nope
            build: { source: ./out }
            """;
        var diags = await SchemaValidator.ValidateAsync(yaml, "x.yaml");
        diags.Should().Contain(d => d.Message.Contains("additional property"));
    }

    [Fact]
    public async Task SignLocalProvider_RequiresLocalSection()
    {
        const string yaml = $"{Header}\nsign:\n  provider: local\n";
        var diags = await SchemaValidator.ValidateAsync(yaml, "x.yaml");
        diags.Should().Contain(d => d.Message.Contains("local") && d.Message.Contains("required"));
    }

    [Fact]
    public async Task SignAzureProvider_RequiresAzureSection()
    {
        const string yaml = $"{Header}\nsign:\n  provider: azure-trusted-signing\n";
        var diags = await SchemaValidator.ValidateAsync(yaml, "x.yaml");
        diags.Should().Contain(d => d.Message.Contains("azureTrustedSigning") && d.Message.Contains("required"));
    }

    [Fact]
    public async Task BadInstallerColor_IsRejected()
    {
        const string yaml = $"{Header}\ninstaller:\n  brand:\n    primaryColor: \"blue\"\n";
        var diags = await SchemaValidator.ValidateAsync(yaml, "x.yaml");
        diags.Should().Contain(d => d.Message.Contains("pattern"));
    }

    [Fact]
    public async Task SchemaDiagnostic_IncludesYamlSourcePosition()
    {
        const string yaml = """
            spec: v1.0
            app:
              id: bad
              name: Example
              version: 0.1.0
              publisher: P
            build: { source: ./out }
            """;
        var diags = await SchemaValidator.ValidateAsync(yaml, "x.yaml");
        var idDiag = diags.FirstOrDefault(d => d.Message.StartsWith("app.id", System.StringComparison.Ordinal));
        idDiag.Should().NotBeNull();
        idDiag!.Location.Line.Should().BeGreaterThan(0);
        idDiag.Location.File.Should().Be("x.yaml");
    }

    [Fact]
    public async Task YamlSyntaxError_ReportsSyntaxCode()
    {
        const string yaml = "spec: v1.0\napp:\n  id: : :\n";
        var diags = await SchemaValidator.ValidateAsync(yaml, "x.yaml");
        diags.Should().ContainSingle().Which.Code.Should().Be(DiagnosticCodes.YamlSyntaxError);
    }
}
