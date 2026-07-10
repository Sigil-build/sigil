using System.Collections.Generic;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Packaging.Installer;
using Xunit;

namespace SigilBuild.Packaging.Tests.Installer;

/// <summary>
/// Golden-file coverage for the T7 two-color palette derivation: a known
/// primary/accent must yield the exact light + dark token maps ported from the
/// prototype's <c>colors()</c> constants, plus focused <see cref="BrandTokenEmitter.SrgbMix"/>
/// unit tests.
/// </summary>
public class BrandTokenDeriveTests
{
    // Prototype defaults: brand = [primary #312E81, accent #4F46E5].
    private const string Primary = "#312E81";
    private const string Accent = "#4F46E5";

    private static SigilManifest Manifest(string primary, string accent) =>
        new("v1.0",
            new AppSection("com.example.App", "Example", "1.0.0", "Example Inc.", null, null),
            new BuildSection("./out", null, null, true),
            null, null, null, null,
            Installer: new InstallerSection(new InstallerBrand(null, null, primary, accent)),
            Location: SourceLocation.Unknown);

    [Fact]
    public void Derive_Light_MatchesPrototypeConstants()
    {
        var palette = BrandTokenEmitter.Derive(Manifest(Primary, Accent));

        var expected = new Dictionary<string, string>
        {
            ["railBg"] = "#312E81",
            ["railText"] = "#FFFFFF",
            ["railMuted"] = "#C3C0F6",   // mix(accent, 34%, #FFFFFF)
            ["logoTile"] = "#3E38AB",    // mix(accent, 42%, rail)
            ["accent"] = "#4F46E5",
            ["accentHover"] = "#463ECA", // mix(accent, 88%, #000000)
            ["frame"] = "#D0D3D9",
            ["winBg"] = "#FFFFFF",
            ["paneBg"] = "#FFFFFF",
            ["titleBg"] = "#F9FAFB",
            ["border"] = "#E5E7EB",
            ["textPri"] = "#111827",
            ["textSec"] = "#4B5563",
            ["textMut"] = "#6B7280",
            ["successText"] = "#0F6E56",
            ["successBg"] = "#E1F5EE",
            ["inputBg"] = "#F9FAFB",
            ["track"] = "#E5E7EB",
            ["logBg"] = "#F9FAFB",
            ["logText"] = "#374151",
            ["dangerText"] = "#B42318",
            ["dangerBg"] = "#FEE4E2",
            ["ghostHover"] = "#F3F4F6",
        };

        palette.Light.Should().Equal(expected);
    }

    [Fact]
    public void Derive_Dark_MatchesPrototypeConstants()
    {
        var palette = BrandTokenEmitter.Derive(Manifest(Primary, Accent));

        var expected = new Dictionary<string, string>
        {
            ["railBg"] = "#2A286F",      // mix(rail, 86%, #000000)
            ["railText"] = "#FFFFFF",
            ["railMuted"] = "#8B89E7",   // mix(accent, 50%, #C7CBE8)
            ["logoTile"] = "#403AB3",    // mix(accent, 50%, rail)
            ["accent"] = "#4F46E5",
            ["accentHover"] = "#6F67EA", // mix(accent, 82%, #FFFFFF)
            ["frame"] = "#000000",
            ["winBg"] = "#14161C",
            ["paneBg"] = "#14161C",
            ["titleBg"] = "#1B1E26",
            ["border"] = "#2A2E38",
            ["textPri"] = "#F3F4F6",
            ["textSec"] = "#C4C9D4",
            ["textMut"] = "#8B90A0",
            ["successText"] = "#34D399",
            ["successBg"] = "#0E2E24",
            ["inputBg"] = "#1B1E26",
            ["track"] = "#2A2E38",
            ["logBg"] = "#0B0D11",
            ["logText"] = "#9CA3AF",
            ["dangerText"] = "#F97066",
            ["dangerBg"] = "#3B1614",
            ["ghostHover"] = "#21242D",
        };

        palette.Dark.Should().Equal(expected);
    }

    [Fact]
    public void Derive_ChangingColors_ReskinsRailAndAccent()
    {
        var a = BrandTokenEmitter.Derive(Manifest("#312E81", "#4F46E5"));
        var b = BrandTokenEmitter.Derive(Manifest("#7C2D12", "#EA580C"));

        a.Light["railBg"].Should().Be("#312E81");
        b.Light["railBg"].Should().Be("#7C2D12");
        a.Light["accent"].Should().Be("#4F46E5");
        b.Light["accent"].Should().Be("#EA580C");
        // Constants stay put regardless of brand.
        a.Light["frame"].Should().Be(b.Light["frame"]);
    }

    [Fact]
    public void Derive_NullBrand_UsesNeutralDefaults()
    {
        var manifest = new SigilManifest("v1.0",
            new AppSection("com.example.App", "Example", "1.0.0", "Example Inc.", null, null),
            new BuildSection("./out", null, null, true),
            null, null, null, null,
            Installer: null, Location: SourceLocation.Unknown);

        var palette = BrandTokenEmitter.Derive(manifest);

        palette.Light["railBg"].Should().Be("#1F2937");
        palette.Light["accent"].Should().Be("#3B82F6");
    }

    // ── SrgbMix unit tests ────────────────────────────────────────────────────

    [Theory]
    [InlineData("#000000", 0, "#FFFFFF", "#FFFFFF")]   // 0% A -> all B
    [InlineData("#000000", 100, "#FFFFFF", "#000000")] // 100% A -> all A
    [InlineData("#000000", 50, "#FFFFFF", "#808080")]  // even blend, round away from zero
    [InlineData("#FF0000", 50, "#0000FF", "#800080")]
    [InlineData("#4F46E5", 34, "#FFFFFF", "#C3C0F6")]  // the light railMuted derivation
    public void SrgbMix_BlendsPerChannel(string a, int pct, string b, string expected)
    {
        BrandTokenEmitter.SrgbMix(a, pct, b).Should().Be(expected);
    }

    [Fact]
    public void SrgbMix_IsCommutativeUnderPercentInversion()
    {
        // mix(A p%, B) == mix(B (100-p)%, A)
        BrandTokenEmitter.SrgbMix("#123456", 30, "#ABCDEF")
            .Should().Be(BrandTokenEmitter.SrgbMix("#ABCDEF", 70, "#123456"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void SrgbMix_RejectsOutOfRangePercent(int pct)
    {
        var act = () => BrandTokenEmitter.SrgbMix("#000000", pct, "#FFFFFF");
        act.Should().Throw<System.ArgumentOutOfRangeException>();
    }
}
