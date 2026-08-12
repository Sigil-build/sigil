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
public sealed class BrandTokens : System.ComponentModel.INotifyPropertyChanged
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
    /// <remarks>
    /// R48: settable and observable rather than <c>init</c>-only, because resolving it
    /// calls <c>WinVerifyTrust</c> — a revocation lookup that reaches the network. It was
    /// resolved inline while the wizard's first window was being built, i.e. on the UI
    /// thread before anything had been drawn: measured at <b>335 ms on the happy path</b>
    /// (online, warm certificate cache, embedded-signed target), which is already past
    /// the ~100 ms at which a UI reads as unresponsive, and every condition that makes it
    /// worse — cold cache, captive portal, unreachable CRL distribution point — moves in
    /// one direction only. It is now resolved on a thread-pool thread and assigned here
    /// when it arrives, so the window paints immediately and the trust line appears a
    /// moment later. The safe default is what renders in the meantime: no line.
    /// </remarks>
    public string? TrustLine
    {
        get => _trustLine;
        set
        {
            if (_trustLine == value)
            {
                return;
            }
            _trustLine = value;
            OnPropertyChanged(nameof(TrustLine));
            OnPropertyChanged(nameof(HasTrustLine));
        }
    }

    private string? _trustLine;

    /// <summary>True when a verified trust line should render.</summary>
    public bool HasTrustLine => !string.IsNullOrEmpty(TrustLine);

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
}
