using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SigilBuild.Installer.Host.Branding;

public sealed class BrandTokens
{
    public string AppName { get; init; } = "Application";
    public string AppVersion { get; init; } = "1.0.0";
    public string Publisher { get; init; } = "Publisher";
    public string PrimaryColor { get; init; } = "#1F2937";
    public string AccentColor { get; init; } = "#3B82F6";
    public string GradientStart { get; init; } = "#0F172A";
    public string GradientMid { get; init; } = "#1E1B4B";
    public string GradientEnd { get; init; } = "#4F46E5";
    public string LogoFile { get; init; } = "default-logo.png";
    public string HeroFile { get; init; } = "default-hero.png";

    public static BrandTokens LoadOrDefault(string sideloadPath)
    {
        if (!File.Exists(sideloadPath)) return new BrandTokens();
        return JsonSerializer.Deserialize(
            File.ReadAllText(sideloadPath),
            BrandTokensJsonContext.Default.BrandTokens) ?? new BrandTokens();
    }
}

// PropertyNamingPolicy = CamelCase aligns the source-gen with the camelCase
// keys emitted by SigilBuild.Packaging.Installer.BrandTokenEmitter
// (primaryColor, accentColor, gradientStart, gradientMid, gradientEnd, …).
// Without this, deserialization is case-sensitive against PascalCase property
// names and every brand token silently keeps its hard-coded default — except
// when the AOT runtime mismatches and one or more properties end up null,
// which crashes BrandPalette.Apply with ArgumentNullException("Value cannot
// be null. (Parameter 's')") inside Color.Parse.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BrandTokens))]
internal sealed partial class BrandTokensJsonContext : JsonSerializerContext { }
