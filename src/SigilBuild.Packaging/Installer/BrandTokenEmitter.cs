using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using SigilBuild.Core.Manifest;
using SigilBuild.Installer.BrandGenerator;

namespace SigilBuild.Packaging.Installer;

public static class BrandTokenEmitter
{
    private const string DefaultPrimary = "#1F2937";
    private const string DefaultAccent = "#3B82F6";
    private const string DefaultGradientStart = "#0F172A";
    private const string DefaultGradientMid = "#1E1B4B";
    private const string DefaultGradientEnd = "#4F46E5";

    public sealed record EmitResult(string Json, IReadOnlyList<string> Warnings);

    public static EmitResult EmitWithDiagnostics(SigilManifest manifest)
    {
        var warnings = new List<string>();
        var json = EmitInternal(manifest, warnings);
        return new EmitResult(json, warnings);
    }

    public static string Emit(SigilManifest manifest, bool allowLowContrast = true)
    {
        var warnings = new List<string>();
        var json = EmitInternal(manifest, warnings);
        if (!allowLowContrast && warnings.Count > 0)
            throw new InvalidOperationException(
                $"{string.Join("; ", warnings)} — pass --allow-low-contrast to override.");
        return json;
    }

    private static string EmitInternal(SigilManifest manifest, List<string> warnings)
    {
        var brand = manifest.Installer?.Brand;
        var primary = brand?.PrimaryColor ?? DefaultPrimary;
        var accent = brand?.AccentColor ?? DefaultAccent;
        var gStart = brand?.GradientStart ?? DefaultGradientStart;
        var gMid = brand?.GradientMid ?? DefaultGradientMid;
        var gEnd = brand?.GradientEnd ?? DefaultGradientEnd;

        if (!WcagContrast.PassesAaAgainstWhite(primary))
            warnings.Add($"installer.brand.primaryColor '{primary}' fails WCAG AA (4.5:1) against white text");

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("appName", manifest.App.Name);
            writer.WriteString("appVersion", manifest.App.Version);
            writer.WriteString("publisher", manifest.App.Publisher);
            writer.WriteString("primaryColor", primary);
            writer.WriteString("accentColor", accent);
            writer.WriteString("gradientStart", gStart);
            writer.WriteString("gradientMid", gMid);
            writer.WriteString("gradientEnd", gEnd);
            writer.WriteString("logoFile", brand?.Logo ?? "default-logo.png");
            writer.WriteString("heroFile", brand?.Hero ?? "default-hero.png");
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
