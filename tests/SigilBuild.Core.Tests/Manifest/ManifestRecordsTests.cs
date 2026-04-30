using FluentAssertions;
using SigilBuild.Core.Manifest;
using Xunit;

namespace SigilBuild.Core.Tests.Manifest;

public class ManifestRecordsTests
{
    [Fact]
    public void SigilManifest_HasRequiredSections()
    {
        var manifest = new SigilManifest(
            Spec: "v1.0",
            App: new AppSection("com.example.App", "Example", "0.1.0", "Example Inc.", null, null),
            Build: new BuildSection("./out", null, null, true),
            Package: null, Sign: null, Publish: null, Updates: null, Installer: null,
            Location: SourceLocation.Unknown);

        manifest.Spec.Should().Be("v1.0");
        manifest.App.Id.Should().Be("com.example.App");
        manifest.Build.Source.Should().Be("./out");
    }

    [Fact]
    public void SourceLocation_Unknown_HasZeroLineAndColumn()
    {
        SourceLocation.Unknown.Line.Should().Be(0);
        SourceLocation.Unknown.Column.Should().Be(0);
        SourceLocation.Unknown.File.Should().BeEmpty();
    }
}
