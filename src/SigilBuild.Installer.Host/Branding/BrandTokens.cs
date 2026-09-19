// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using System.Collections.Generic;
using System.IO;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SigilBuild.Wrapper.Core.Localization;

namespace SigilBuild.Installer.Host.Branding;

/// <summary>
/// Brand data consumed by the wizard. The full light/dark palette is derived at
/// pack time (Avalonia cannot <c>color-mix</c> at runtime) and delivered inside
/// the WrapperBlob — there is no <c>BrandTokens.g.json</c> sidecar for a stamped
/// <c>.exe</c>. <see cref="LightTokens"/> / <see cref="DarkTokens"/>
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
    /// The logo to render: the manifest's <c>installer.brand.logo</c> when one was
    /// packed, otherwise the bundled default. Also the window icon, so the title
    /// bar, the taskbar and the rail all show the same mark.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Until this existed, <see cref="LogoBase64"/> was read out of the blob into
    /// this type and then consumed by nothing: all three windows bound their
    /// <c>Image</c> to the bundled asset by a literal <c>avares://</c> path, so a
    /// manifest that declared a brand logo had it packed and silently ignored, and
    /// no window set <c>Icon</c> at all.
    /// </para>
    /// <para>
    /// Resolved lazily and once. Lazily because constructing an Avalonia
    /// <see cref="Bitmap"/> needs an initialised platform, and ~20 test classes
    /// build a <c>BrandTokens</c> with no UI at all; once because the getter is hit
    /// on every screen change.
    /// </para>
    /// <para>
    /// A brand logo that will not decode falls back to the default rather than
    /// throwing. A decorative image is never worth failing an install over — and a
    /// bitmap that Skia refuses, thrown out of a window constructor, is exactly the
    /// defect that kept this wizard from ever opening.
    /// </para>
    /// </remarks>
    public Bitmap? LogoImage
    {
        get
        {
            if (_logoResolved)
            {
                return _logo;
            }

            _logoResolved = true;
            _logo = DecodeBrandLogo() ?? LoadDefaultLogo();
            return _logo;
        }
    }

    private Bitmap? _logo;
    private bool _logoResolved;

    private Bitmap? DecodeBrandLogo()
    {
        if (string.IsNullOrWhiteSpace(LogoBase64))
        {
            return null;
        }

#pragma warning disable CA1031 // Any failure here means "no usable brand logo"; the default is the answer.
        try
        {
            using var stream = new MemoryStream(System.Convert.FromBase64String(LogoBase64));
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
#pragma warning restore CA1031
    }

    private static Bitmap? LoadDefaultLogo()
    {
#pragma warning disable CA1031 // A missing default asset must not take the wizard down with it.
        try
        {
            using var stream = AssetLoader.Open(new System.Uri("avares://installer/Assets/default-logo.png"));
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// The verified-signature-gated trust line, e.g.
    /// <c>"Signed by Acme, Inc."</c>. Non-null ONLY when the manifest declared a
    /// <c>sign</c> block AND the running exe's Authenticode signature verified;
    /// <c>null</c> for an unsigned, un-stamped, or tampered/re-stamped artifact —
    /// the wizard then shows no trust line (the neutral publisher name still
    /// renders separately). <see cref="HasTrustLine"/> drives its visibility.
    /// </summary>
    /// <remarks>
    /// Settable and observable rather than <c>init</c>-only, because resolving it calls
    /// <c>WinVerifyTrust</c> — a revocation lookup that reaches the network, measured at
    /// <b>335 ms on the happy path</b> (online, warm certificate cache, embedded-signed
    /// target) and only worse on a cold cache, a captive portal or an unreachable CRL
    /// distribution point. Resolving it on the UI thread while the first window is built
    /// would hold the paint past the ~100 ms at which a UI reads as unresponsive, so
    /// <c>TrustLineActivation</c> resolves it on a thread-pool thread and assigns it here
    /// when it arrives. The safe default renders in the meantime: no line. (R48)
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
