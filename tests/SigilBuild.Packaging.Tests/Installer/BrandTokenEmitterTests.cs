using System.Text.Json;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Packaging.Installer;
using Xunit;

namespace SigilBuild.Packaging.Tests.Installer;

public class BrandTokenEmitterTests
{
    [Fact]
    public void Emit_AppliesDefaultsWhenBrandIsNull()
    {
        var manifest = new SigilManifest("v1.0",
            new AppSection("com.example.App", "Example", "1.0.0", "Example Inc.", null, null),
            new BuildSection("./out", null, null, true), null, null, null, null,
            Installer: null, Location: SourceLocation.Unknown);

        var json = BrandTokenEmitter.Emit(manifest);
        var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("primaryColor").GetString().Should().Be("#1F2937");
        doc.RootElement.GetProperty("accentColor").GetString().Should().Be("#3B82F6");
        doc.RootElement.GetProperty("appName").GetString().Should().Be("Example");
    }

    [Fact]
    public void Emit_WarnsButSucceedsWhenPrimaryColorViolatesWcagAA()
    {
        var manifest = new SigilManifest("v1.0",
            new AppSection("com.example.App", "Example", "1.0.0", "Example Inc.", null, null),
            new BuildSection("./out", null, null, true), null, null, null, null,
            Installer: new InstallerSection(new InstallerBrand(null, null, "#FFEE00", "#3B82F6", null, null, null), null),
            Location: SourceLocation.Unknown);

        var result = BrandTokenEmitter.EmitWithDiagnostics(manifest);
        result.Json.Should().NotBeNullOrEmpty();
        result.Warnings.Should().ContainSingle(w => w.Contains("WCAG AA"));
    }

    [Fact]
    public void Emit_BlocksWhenLowContrastAndOverrideNotSet()
    {
        var manifest = new SigilManifest("v1.0",
            new AppSection("com.example.App", "Example", "1.0.0", "Example Inc.", null, null),
            new BuildSection("./out", null, null, true), null, null, null, null,
            Installer: new InstallerSection(new InstallerBrand(null, null, "#FFEE00", "#3B82F6", null, null, null), null),
            Location: SourceLocation.Unknown);

        var act = () => BrandTokenEmitter.Emit(manifest, allowLowContrast: false);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*WCAG AA*--allow-low-contrast*");
    }
}
