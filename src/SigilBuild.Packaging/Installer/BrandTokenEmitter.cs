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
        var gStart = brand?.GradientStart ?? DefaultGradientStart;
        var gMid = brand?.GradientMid ?? DefaultGradientMid;
        var gEnd = brand?.GradientEnd ?? DefaultGradientEnd;

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
            writer.WriteString("gradientStart", gStart);
            writer.WriteString("gradientMid", gMid);
            writer.WriteString("gradientEnd", gEnd);
            writer.WriteString("logoFile", logoFile);
            writer.WriteString("heroFile", brand?.Hero ?? "default-hero.png");
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
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
                    if (param.Description is not null) writer.WriteString("description", param.Description);
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
