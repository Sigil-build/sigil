using System.Xml.Linq;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Packaging.Msix;
using Xunit;

namespace SigilBuild.Packaging.Tests.Msix;

public class AppxManifestBuilderTests
{
    private static SigilManifest BuildManifest() => new(
        Spec: "v1.0",
        App: new AppSection("com.example.App", "Example", "1.2.3", "Example Inc.", null, null),
        Build: new BuildSection("./out", null, null, true),
        Package: new PackageSection(
            new[] { PackageFormat.Msix },
            new[] { TargetArchitecture.X64 },
            new MsixOptions("CN=Example Inc.", "logo.png", new[] { "internetClient" })),
        Sign: null, Publish: null, Updates: null, Installer: null,
        Location: SourceLocation.Unknown);

    [Fact]
    public void Build_HasIdentityAndPropertiesAndCapabilities()
    {
        var xml = AppxManifestBuilder.Build(BuildManifest(), TargetArchitecture.X64);

        var doc = XDocument.Parse(xml);
        XNamespace ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";

        var identity = doc.Root!.Element(ns + "Identity")!;
        identity.Attribute("Name")!.Value.Should().Be("com.example.App");
        // MSIX requires 4-part Quad version: appends ".0" if needed
        identity.Attribute("Version")!.Value.Should().Be("1.2.3.0");
        identity.Attribute("Publisher")!.Value.Should().Be("CN=Example Inc.");
        identity.Attribute("ProcessorArchitecture")!.Value.Should().Be("x64");

        var props = doc.Root.Element(ns + "Properties")!;
        props.Element(ns + "DisplayName")!.Value.Should().Be("Example");
        props.Element(ns + "PublisherDisplayName")!.Value.Should().Be("Example Inc.");

        var capsElement = doc.Root.Element(ns + "Capabilities")!;
        capsElement.Elements(ns + "Capability")
            .Should().ContainSingle(c => c.Attribute("Name")!.Value == "internetClient");

        // Executable derives from last segment of App.Id, not App.Name (which may contain spaces).
        var app = doc.Root.Element(ns + "Applications")!.Element(ns + "Application")!;
        app.Attribute("Executable")!.Value.Should().Be("App.exe");
    }

    [Fact]
    public void Build_SpacedAppName_ExecutableDerivesFromId()
    {
        var manifest = new SigilManifest(
            Spec: "v1.0",
            App: new AppSection("com.example.LocalSignedApp", "Local-Signed App", "1.2.3", "Example Inc.", null, null),
            Build: new BuildSection("./out", null, null, true),
            Package: new PackageSection(
                new[] { PackageFormat.Msix },
                new[] { TargetArchitecture.X64 },
                new MsixOptions("CN=Example Inc.", null, null)),
            Sign: null, Publish: null, Updates: null, Installer: null,
            Location: SourceLocation.Unknown);

        var xml = AppxManifestBuilder.Build(manifest, TargetArchitecture.X64);
        XNamespace ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        var app = XDocument.Parse(xml).Root!.Element(ns + "Applications")!.Element(ns + "Application")!;
        app.Attribute("Executable")!.Value.Should().Be("LocalSignedApp.exe");
    }

    [Fact]
    public void Build_WithArm64_PutsArm64InIdentity()
    {
        var xml = AppxManifestBuilder.Build(BuildManifest(), TargetArchitecture.Arm64);
        XNamespace ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        var identity = XDocument.Parse(xml).Root!.Element(ns + "Identity")!;
        identity.Attribute("ProcessorArchitecture")!.Value.Should().Be("arm64");
    }
}
