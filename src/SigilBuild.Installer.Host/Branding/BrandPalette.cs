using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace SigilBuild.Installer.Host.Branding;

/// <summary>
/// Projects the derived brand token map onto Avalonia application resources.
/// Each token <c>k</c> becomes a <c>{Pascal(k)}Color</c> and <c>{Pascal(k)}Brush</c>
/// resource (e.g. <c>railBg</c> → <c>RailBgColor</c> / <c>RailBgBrush</c>), which
/// the wizard XAML binds via <c>DynamicResource</c>. Literal fallbacks for every
/// key live in <c>BrandPalette.axaml</c>, so an un-branded/dev run still renders.
/// </summary>
public static class BrandPalette
{
    public static void Apply(Application app, BrandTokens tokens)
    {
        ArgumentNullException.ThrowIfNull(app);
        var isDark = app.ActualThemeVariant == ThemeVariant.Dark;
        Apply(app, tokens, isDark);
    }

    public static void Apply(Application app, BrandTokens tokens, bool isDark)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(tokens);

        var map = isDark ? tokens.DarkTokens : tokens.LightTokens;

        // Retain the two-color source resources for any consumer that still
        // references them directly.
        app.Resources["PrimaryColor"] = Color.Parse(tokens.PrimaryColor);
        app.Resources["AccentColor"] = Color.Parse(tokens.AccentColor);
        app.Resources["PrimaryBrush"] = new SolidColorBrush(Color.Parse(tokens.PrimaryColor));

        foreach (var kv in map)
        {
            if (!TryParseColor(kv.Value, out var color))
                continue;

            var pascal = Pascal(kv.Key);
            app.Resources[pascal + "Color"] = color;
            app.Resources[pascal + "Brush"] = new SolidColorBrush(color);
        }
    }

    private static bool TryParseColor(string hex, out Color color)
    {
        try
        {
            color = Color.Parse(hex);
            return true;
        }
        catch (FormatException)
        {
            color = default;
            return false;
        }
    }

    private static string Pascal(string key)
    {
        if (string.IsNullOrEmpty(key)) return key;
        if (char.IsUpper(key[0])) return key;
        return string.Create(key.Length, key, static (span, src) =>
        {
            src.AsSpan().CopyTo(span);
            span[0] = char.ToUpperInvariant(span[0]);
        });
    }
}
