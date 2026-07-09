using System.IO;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Packaging.ExeWrapper;
using SigilBuild.Wrapper.Json;
using Xunit;

namespace SigilBuild.Packaging.Tests.ExeWrapper;

/// <summary>
/// Verifies the pack → blob half of the T7 brand flow: <see cref="ExeWrapperPackager.BuildBlobBytes"/>
/// derives the palette and embeds the light/dark token maps + base64 logo/hero
/// into the <c>SIGIL_BLOB_V1</c> wire payload (no sidecar). The blob → host half
/// is exercised by the host tests.
/// </summary>
public class ExeWrapperBlobBrandTests
{
    private static SerializableWrapperBlob Deserialize(byte[] blob) =>
        JsonSerializer.Deserialize(
            Encoding.UTF8.GetString(blob),
            WrapperBlobJsonContext.Default.SerializableWrapperBlob)!;

    [Fact]
    public void BuildBlobBytes_EmbedsDerivedTokensAndLogo()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sigil-brand-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var logoBytes = new byte[] { 1, 2, 3, 4, 5 };
            File.WriteAllBytes(Path.Combine(dir, "logo.png"), logoBytes);

            var manifest = new SigilManifest("v1.0",
                new AppSection("com.example.App", "Example", "1.0.0", "Example Inc.", null, null),
                new BuildSection("./out", null, null, true),
                null, null, null, null,
                Installer: new InstallerSection(new InstallerBrand("logo.png", null, "#312E81", "#4F46E5")),
                Location: SourceLocation.Unknown);

            var blob = ExeWrapperPackager.BuildBlobBytes(manifest, dir);
            var s = Deserialize(blob);

            s.BrandTokensLight.Should().NotBeNull();
            s.BrandTokensLight!["railBg"].Should().Be("#312E81");
            s.BrandTokensLight!["accent"].Should().Be("#4F46E5");
            s.BrandTokensLight!["railMuted"].Should().Be("#C3C0F6");

            s.BrandTokensDark.Should().NotBeNull();
            s.BrandTokensDark!["railBg"].Should().Be("#2A286F");

            s.LogoBase64.Should().Be(System.Convert.ToBase64String(logoBytes));
            s.HeroBase64.Should().BeNull();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void BuildBlobBytes_EmbedsDeclaredScreensAndParameters()
    {
        var parameters = new System.Collections.Generic.Dictionary<string, ParameterDefinition>
        {
            ["channel"] = new("channel", ParameterType.Enum, "stable",
                new[] { "stable", "beta" }, true, "Update channel", null, null, null),
        };
        var screens = new System.Collections.Generic.List<InstallerScreen>
        {
            new("configure", "Configure {app.name}", "sub", null,
                new System.Collections.Generic.List<ScreenField> { new("channel", "radio") }),
        };

        var manifest = new SigilManifest("v1.0",
            new AppSection("com.example.App", "Example", "1.0.0", "Example Inc.", null, null),
            new BuildSection("./out", null, null, true),
            null, null, null, null,
            Installer: new InstallerSection(null, Screens: screens),
            Location: SourceLocation.Unknown,
            Parameters: parameters);

        var blob = ExeWrapperPackager.BuildBlobBytes(manifest, string.Empty);
        var s = Deserialize(blob);

        s.Screens.Should().ContainSingle();
        s.Screens[0].Id.Should().Be("configure");
        s.Screens[0].Title.Should().Be("Configure {app.name}");
        s.Screens[0].Fields.Should().ContainSingle();
        s.Screens[0].Fields[0].Param.Should().Be("channel");
        s.Screens[0].Fields[0].Widget.Should().Be("radio");

        s.Parameters.Should().ContainSingle(p => p.Name == "channel");
    }

    [Fact]
    public void BuildBlobBytes_NoBrand_StillDerivesDefaultPaletteWithoutAssets()
    {
        var manifest = new SigilManifest("v1.0",
            new AppSection("com.example.App", "Example", "1.0.0", "Example Inc.", null, null),
            new BuildSection("./out", null, null, true),
            null, null, null, null,
            Installer: null, Location: SourceLocation.Unknown);

        var blob = ExeWrapperPackager.BuildBlobBytes(manifest, string.Empty);
        var s = Deserialize(blob);

        s.BrandTokensLight.Should().NotBeNull();
        s.BrandTokensLight!["railBg"].Should().Be("#1F2937");
        s.LogoBase64.Should().BeNull();
        s.HeroBase64.Should().BeNull();
    }
}
