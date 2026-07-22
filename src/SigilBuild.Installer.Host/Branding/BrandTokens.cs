using System.Collections.Generic;
using SigilBuild.Wrapper.Core.Localization;

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
    public string AppName { get; init; } = Strings.BrandAppFallback(SessionLanguage.Current);
    public string AppVersion { get; init; } = "1.0.0";
    public string Publisher { get; init; } = Strings.BrandPublisherFallback(SessionLanguage.Current);
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

    /// <summary>
    /// The verified-signature-gated trust line (T11 / decision 7), e.g.
    /// <c>"Signed by Acme, Inc."</c>. Non-null ONLY when the manifest declared a
    /// <c>sign</c> block AND the running exe's Authenticode signature verified;
    /// <c>null</c> for an unsigned, un-stamped, or tampered/re-stamped artifact —
    /// the wizard then shows no trust line (the neutral publisher name still
    /// renders separately). <see cref="HasTrustLine"/> drives its visibility.
    /// </summary>
    public string? TrustLine { get; init; }

    /// <summary>True when a verified trust line should render.</summary>
    public bool HasTrustLine => !string.IsNullOrEmpty(TrustLine);
}
