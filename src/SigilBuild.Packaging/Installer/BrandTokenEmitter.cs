using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using SigilBuild.Core.Manifest;
using SigilBuild.Installer.BrandGenerator;

namespace SigilBuild.Packaging.Installer;

/// <summary>
/// Derives the full light + dark installer palette from the two-color brand
/// (<c>primary_color</c> + <c>accent_color</c>) at pack time — Avalonia cannot
/// <c>color-mix</c> at runtime, so every token is resolved here and travels
/// inside the WrapperBlob (see decision 11).
/// </summary>
/// <remarks>
/// The token derivation ports the prototype's <c>colors()</c> function
/// (<c>docs/plan/prototype/sigil-installer-wizard-prototype.html</c>) verbatim,
/// including its literal constants and the <c>color-mix(in srgb, …)</c> blends,
/// implemented here by <see cref="SrgbMix"/>.
/// </remarks>
public static class BrandTokenEmitter
{
    private const string DefaultPrimary = "#1F2937";
    private const string DefaultAccent = "#3B82F6";

    /// <summary>Result of <see cref="Derive"/>: both palette maps + any diagnostics.</summary>
    public sealed record DerivedPalette(
        IReadOnlyDictionary<string, string> Light,
        IReadOnlyDictionary<string, string> Dark,
        IReadOnlyList<string> Warnings);

    public sealed record EmitResult(string Json, IReadOnlyList<string> Warnings);

    /// <summary>
    /// Per-channel linear interpolation of two sRGB colors, matching CSS
    /// <c>color-mix(in srgb, <paramref name="hexA"/> <paramref name="pct"/>%,
    /// <paramref name="hexB"/>)</c>:
    /// <c>out_c = round(A_c*(p/100) + B_c*(1 - p/100))</c> per 0–255 channel.
    /// </summary>
    public static string SrgbMix(string hexA, int pct, string hexB)
    {
        ArgumentNullException.ThrowIfNull(hexA);
        ArgumentNullException.ThrowIfNull(hexB);
        if (pct is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(pct), pct, "percentage must be 0–100");

        var (ar, ag, ab) = ParseRgb(hexA);
        var (br, bg, bb) = ParseRgb(hexB);
        var p = pct / 100.0;

        int Mix(int a, int b) =>
            (int)Math.Round(a * p + b * (1 - p), MidpointRounding.AwayFromZero);

        return $"#{Mix(ar, br):X2}{Mix(ag, bg):X2}{Mix(ab, bb):X2}";
    }

    /// <summary>
    /// Derive the full light + dark token maps from the manifest brand.
    /// Falls back to the neutral default primary/accent when unset.
    /// </summary>
    public static DerivedPalette Derive(SigilManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var brand = manifest.Installer?.Brand;
        var primary = Normalize(brand?.PrimaryColor) ?? DefaultPrimary;
        var accent = Normalize(brand?.AccentColor) ?? DefaultAccent;

        var light = DeriveLight(primary, accent);
        var dark = DeriveDark(primary, accent);

        var warnings = new List<string>();
        // Primary is the rail background carrying white rail text.
        if (!WcagContrast.PassesAaAgainstWhite(primary))
            warnings.Add($"installer.brand.primary_color '{primary}' fails WCAG AA (4.5:1) against white text");
        // Rail-muted is secondary text drawn on the rail background; check it too.
        if (!WcagContrast.PassesAa(light["railMuted"], light["railBg"]))
            warnings.Add($"derived light railMuted '{light["railMuted"]}' fails WCAG AA (4.5:1) against railBg '{light["railBg"]}'");

        return new DerivedPalette(light, dark, warnings);
    }

    // ── Prototype colors() port ───────────────────────────────────────────────
    // rail = primary_color, accent = accent_color.

    private static Dictionary<string, string> DeriveLight(string rail, string accent) => new()
    {
        ["railBg"] = rail,
        ["railText"] = "#FFFFFF",
        ["railMuted"] = SrgbMix(accent, 34, "#FFFFFF"),
        ["logoTile"] = SrgbMix(accent, 42, rail),
        ["accent"] = accent,
        ["accentHover"] = SrgbMix(accent, 88, "#000000"),
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

    private static Dictionary<string, string> DeriveDark(string rail, string accent) => new()
    {
        ["railBg"] = SrgbMix(rail, 86, "#000000"),
        ["railText"] = "#FFFFFF",
        ["railMuted"] = SrgbMix(accent, 50, "#C7CBE8"),
        ["logoTile"] = SrgbMix(accent, 50, rail),
        ["accent"] = accent,
        ["accentHover"] = SrgbMix(accent, 82, "#FFFFFF"),
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

    // ── Legacy flat JSON emit (still consumed by the MSIX host bundler, T16) ──

    public static EmitResult EmitWithDiagnostics(SigilManifest manifest)
    {
        var warnings = new List<string>();
        var json = EmitInternal(manifest, warnings, bundledLogoFileName: null);
        return new EmitResult(json, warnings);
    }

    public static string Emit(SigilManifest manifest, bool allowLowContrast = true, string? bundledLogoFileName = null)
    {
        var warnings = new List<string>();
        var json = EmitInternal(manifest, warnings, bundledLogoFileName);
        if (!allowLowContrast && warnings.Count > 0)
            throw new InvalidOperationException(
                $"{string.Join("; ", warnings)} — pass --allow-low-contrast to override.");
        return json;
    }

    private static string EmitInternal(SigilManifest manifest, List<string> warnings, string? bundledLogoFileName)
    {
        var brand = manifest.Installer?.Brand;
        var primary = brand?.PrimaryColor ?? DefaultPrimary;
        var accent = brand?.AccentColor ?? DefaultAccent;

        if (!WcagContrast.PassesAaAgainstWhite(primary))
            warnings.Add($"installer.brand.primaryColor '{primary}' fails WCAG AA (4.5:1) against white text");

        // Prefer the bundled-logo filename (e.g. "brand-logo.svg") emitted by
        // the EXE-wrapper packager — when the wizard reads its BrandTokens
        // at install time, Path.Combine(wizardDir, this name) needs to
        // resolve to a real file. The user's manifest-relative path
        // ("./brand/foo.svg") only works on the build machine.
        var logoFile = bundledLogoFileName ?? brand?.Logo ?? "default-logo.png";

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("appName", manifest.App.Name);
            writer.WriteString("appVersion", manifest.App.Version);
            writer.WriteString("publisher", manifest.App.Publisher);
            writer.WriteString("primaryColor", primary);
            writer.WriteString("accentColor", accent);
            writer.WriteString("logoFile", logoFile);
            writer.WriteString("heroFile", brand?.Hero ?? "default-hero.png");
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string? Normalize(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        return hex.Trim();
    }

    private static (int r, int g, int b) ParseRgb(string hex)
    {
        var s = hex.AsSpan().TrimStart('#');
        if (s.Length != 6)
            throw new ArgumentException($"expected #RRGGBB, got '{hex}'", nameof(hex));
        return (
            int.Parse(s.Slice(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            int.Parse(s.Slice(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            int.Parse(s.Slice(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Emits the install-time parameter contract the wizard needs to render
    /// its Install Options screen with sigil.yaml's declared defaults
    /// preselected. The shape is a JSON array of <c>{ name, type, default,
    /// description, install_time, values }</c> records — one entry per
    /// declared parameter where <c>install_time == true</c>.
    /// </summary>
    /// <remarks>
    /// We deliberately serialize only the install-time subset. Pack-time
    /// parameters are baked into the wrapper at build and have no role in the
    /// interactive UI. The wizard binds its Install Options widgets to these
    /// entries and writes user overrides back through the wrapper's
    /// <c>/Name=value</c> CLI when launching the install subprocess.
    /// </remarks>
    public static string EmitInstallTimeParameters(SigilManifest manifest)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartArray();
            if (manifest.Parameters is not null)
            {
                foreach (var kv in manifest.Parameters)
                {
                    var param = kv.Value;
                    if (!param.InstallTime) continue;

                    writer.WriteStartObject();
                    writer.WriteString("name", kv.Key);
                    writer.WriteString("type", param.Type.ToString().ToLowerInvariant());
                    writer.WriteBoolean("installTime", param.InstallTime);
                    if (param.Description is not null) writer.WriteString("description", param.Description.English);
                    if (param.EnumValues is not null)
                    {
                        writer.WritePropertyName("values");
                        writer.WriteStartArray();
                        foreach (var v in param.EnumValues) writer.WriteStringValue(v);
                        writer.WriteEndArray();
                    }
                    if (param.Source is not null)
                    {
                        writer.WritePropertyName("source");
                        writer.WriteStartObject();
                        writer.WriteString("url", param.Source.Url);
                        writer.WriteString("itemsPath", param.Source.ItemsPath);
                        writer.WriteString("valueProperty", param.Source.ValueProperty);
                        writer.WriteString("labelProperty", param.Source.LabelProperty);
                        writer.WriteEndObject();
                    }
                    if (!string.IsNullOrEmpty(param.Screen))
                    {
                        writer.WriteString("screen", param.Screen);
                    }
                    WriteDefault(writer, param.Default);
                    writer.WriteEndObject();
                }
            }
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteDefault(Utf8JsonWriter writer, object? value)
    {
        if (value is null) return;
        writer.WritePropertyName("default");
        switch (value)
        {
            case string s: writer.WriteStringValue(s); break;
            case bool b: writer.WriteBooleanValue(b); break;
            case int i: writer.WriteNumberValue(i); break;
            case long l: writer.WriteNumberValue(l); break;
            case double d: writer.WriteNumberValue(d); break;
            default: writer.WriteStringValue(value.ToString()); break;
        }
    }
}
