using FluentAssertions;
using SigilBuild.Core.Configuration;
using SigilBuild.Core.Manifest;
using Xunit;

namespace SigilBuild.Core.Tests.Manifest;

public class ManifestParserFullTests
{
    private const string FullYaml = """
        spec: v1.0
        app:
          id: com.example.Full
          name: Full
          version: 1.2.3
          publisher: Example Inc.
          description: Full coverage manifest
          homepage: https://example.com
        build:
          source: ./out
          include: ["**/*"]
          exclude: ["**/*.pdb"]
          deterministic: false
        package:
          formats: [msix, zip]
          architectures: [x64, arm64]
          msix:
            publisher: "CN=Example Inc."
            logo: ./logo.png
            capabilities: [internetClient]
        sign:
          provider: azure-trusted-signing
          local:
            pfx: ./codesign.pfx
            passwordEnv: PFX_PASS
            timestampUrl: http://timestamp.example.com
          azureTrustedSigning:
            endpoint: https://eus.codesigning.azure.net/
            accountName: acc
            certificateProfile: prof
            tenantIdEnv: AZURE_TENANT
            clientIdEnv: AZURE_CLIENT
            clientSecretEnv: AZURE_SECRET
        publish:
          github:
            repo: org/app
            tagPrefix: ver-
            draft: true
        updates:
          channel: beta
          manifestUrl: https://updates.example.com/manifest.json
          deltaTargets: 5
          signingKey: ./key.pem
        installer:
          brand:
            logo: ./brand/logo.svg
            hero: ./brand/hero.png
            primaryColor: "#1F2937"
            accentColor: "#3B82F6"
        """;

    [Fact]
    public void Parse_FullManifest_ReadsEverySection()
    {
        var result = ManifestParser.Parse(FullYaml, "full.yaml");

        result.Diagnostics.Should().BeEmpty();
        var m = result.Manifest;
        m.Should().NotBeNull();

        m!.App.Description.Should().Be("Full coverage manifest");
        m.App.Homepage.Should().Be("https://example.com");

        m.Build.Include.Should().Equal("**/*");
        m.Build.Exclude.Should().Equal("**/*.pdb");
        m.Build.Deterministic.Should().BeFalse();

        m.Package!.Formats.Should().Equal(PackageFormat.Msix, PackageFormat.Zip);
        m.Package.Architectures.Should().Equal(TargetArchitecture.X64, TargetArchitecture.Arm64);
        m.Package.Msix!.Publisher.Should().Be("CN=Example Inc.");
        m.Package.Msix.Logo.Should().Be("./logo.png");
        m.Package.Msix.Capabilities.Should().Equal("internetClient");

        m.Sign!.Provider.Should().Be(SignProvider.AzureTrustedSigning);
        m.Sign.Local!.Pfx.Should().Be("./codesign.pfx");
        m.Sign.Local.PasswordEnv.Should().Be("PFX_PASS");
        m.Sign.Local.TimestampUrl.Should().Be("http://timestamp.example.com");
        m.Sign.AzureTrustedSigning!.Endpoint.Should().Be("https://eus.codesigning.azure.net/");
        m.Sign.AzureTrustedSigning.AccountName.Should().Be("acc");
        m.Sign.AzureTrustedSigning.CertificateProfile.Should().Be("prof");
        m.Sign.AzureTrustedSigning.TenantIdEnv.Should().Be("AZURE_TENANT");
        m.Sign.AzureTrustedSigning.ClientIdEnv.Should().Be("AZURE_CLIENT");
        m.Sign.AzureTrustedSigning.ClientSecretEnv.Should().Be("AZURE_SECRET");

        m.Publish!.GitHub!.Repo.Should().Be("org/app");
        m.Publish.GitHub.TagPrefix.Should().Be("ver-");
        m.Publish.GitHub.Draft.Should().BeTrue();

        m.Updates!.Channel.Should().Be("beta");
        m.Updates.ManifestUrl.Should().Be("https://updates.example.com/manifest.json");
        m.Updates.DeltaTargets.Should().Be(5);
        m.Updates.SigningKey.Should().Be("./key.pem");

        m.Installer!.Brand!.Logo.Should().Be("./brand/logo.svg");
        m.Installer.Brand.Hero.Should().Be("./brand/hero.png");
        m.Installer.Brand.PrimaryColor.Should().Be("#1F2937");
        m.Installer.Brand.AccentColor.Should().Be("#3B82F6");
    }

    [Fact]
    public void Parse_LocalSignDefaultsTimestampUrl()
    {
        const string yaml = """
            spec: v1.0
            app: { id: com.example.App, name: App, version: 0.1.0, publisher: P }
            build: { source: ./out }
            sign:
              provider: local
              local:
                pfx: ./cert.pfx
            """;

        var m = ManifestParser.Parse(yaml, "x.yaml").Manifest;

        m!.Sign!.Provider.Should().Be(SignProvider.Local);
        m.Sign.Local!.TimestampUrl.Should().Be("http://timestamp.digicert.com");
    }

    [Fact]
    public void Parse_UpdatesDefaultsApply()
    {
        const string yaml = """
            spec: v1.0
            app: { id: com.example.App, name: App, version: 0.1.0, publisher: P }
            build: { source: ./out }
            updates: {}
            """;

        var m = ManifestParser.Parse(yaml, "x.yaml").Manifest;

        m!.Updates!.Channel.Should().Be("stable");
        m.Updates.DeltaTargets.Should().Be(3);
    }

    [Fact]
    public void Parse_UnknownArchitecture_ReportsSyntaxDiagnostic()
    {
        const string yaml = """
            spec: v1.0
            app: { id: com.example.App, name: App, version: 0.1.0, publisher: P }
            build: { source: ./out }
            package:
              formats: [zip]
              architectures: [x86]
            """;

        var result = ManifestParser.Parse(yaml, "x.yaml");

        result.Manifest.Should().BeNull();
        result.Diagnostics.Should().NotBeEmpty();
    }

    [Fact]
    public void Parse_UnknownFormat_ReportsSyntaxDiagnostic()
    {
        const string yaml = """
            spec: v1.0
            app: { id: com.example.App, name: App, version: 0.1.0, publisher: P }
            build: { source: ./out }
            package:
              formats: [tarball]
            """;

        var result = ManifestParser.Parse(yaml, "x.yaml");

        result.Manifest.Should().BeNull();
        result.Diagnostics.Should().NotBeEmpty();
    }

    [Fact]
    public void Parse_RootMustBeMapping()
    {
        const string yaml = "- a\n- b\n";

        var result = ManifestParser.Parse(yaml, "x.yaml");

        result.Manifest.Should().BeNull();
        result.Diagnostics.Should().ContainSingle();
    }
}
