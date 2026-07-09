using System.Collections.Generic;

namespace SigilBuild.Installer.Host.Branding;

/// <summary>
/// Brand data consumed by the wizard. The full light/dark palette is derived at
/// pack time (Avalonia cannot <c>color-mix</c> at runtime) and delivered inside
/// the WrapperBlob — there is no <c>BrandTokens.g.json</c> sidecar for a stamped
/// <c>.exe</c> (decision 11). <see cref="LightTokens"/> / <see cref="DarkTokens"/>
/// map token names (railBg, accent, winBg, …) to <c>#RRGGBB</c> values.
/// </summary>
public sealed class BrandTokens
{
    public string AppName { get; init; } = "Application";
    public string AppVersion { get; init; } = "1.0.0";
    public string Publisher { get; init; } = "Publisher";
    public string PrimaryColor { get; init; } = "#1F2937";
    public string AccentColor { get; init; } = "#3B82F6";
    public string LogoFile { get; init; } = "default-logo.png";
    public string HeroFile { get; init; } = "default-hero.png";

    /// <summary>Derived light-mode token map. Empty => the palette's literal
    /// defaults (BrandPalette.axaml) are used, e.g. an un-stamped dev run.</summary>
    public IReadOnlyDictionary<string, string> LightTokens { get; init; }
        = new Dictionary<string, string>();

    /// <summary>Derived dark-mode token map.</summary>
    public IReadOnlyDictionary<string, string> DarkTokens { get; init; }
        = new Dictionary<string, string>();

    /// <summary>Base64-encoded brand logo bytes carried in the blob, if any.</summary>
    public string? LogoBase64 { get; init; }

    /// <summary>Base64-encoded brand hero bytes carried in the blob, if any.</summary>
    public string? HeroBase64 { get; init; }
}
