using Avalonia;
using Avalonia.Media;

namespace SigilBuild.Installer.Host.Branding;

public static class BrandPalette
{
    // Safety net for the case where deserialization leaves a token property
    // null (e.g. older AOT runtime + missing JsonNamingPolicy). Without a
    // fallback, Color.Parse(null) throws ArgumentNullException with the
    // unhelpful "Parameter 's'" message and tears down the whole wizard
    // before any UI is painted. Picking neutral defaults keeps the wizard
    // alive and visible; the InstallerLog entry tells operators exactly
    // which token came in null.
    private const string FallbackPrimary       = "#1F2937";
    private const string FallbackAccent        = "#3B82F6";
    private const string FallbackGradientStart = "#0F172A";
    private const string FallbackGradientMid   = "#1E1B4B";
    private const string FallbackGradientEnd   = "#4F46E5";

    public static void Apply(Application app, BrandTokens tokens)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(tokens);

        var primary       = Coalesce("PrimaryColor",   tokens.PrimaryColor,   FallbackPrimary);
        var accent        = Coalesce("AccentColor",    tokens.AccentColor,    FallbackAccent);
        var gradientStart = Coalesce("GradientStart",  tokens.GradientStart,  FallbackGradientStart);
        var gradientMid   = Coalesce("GradientMid",    tokens.GradientMid,    FallbackGradientMid);
        var gradientEnd   = Coalesce("GradientEnd",    tokens.GradientEnd,    FallbackGradientEnd);

        InstallerLog.Info($"BrandPalette.Apply colors: Primary={primary}, Accent={accent}, GradientStart={gradientStart}, GradientMid={gradientMid}, GradientEnd={gradientEnd}");

        app.Resources["PrimaryColor"]       = Color.Parse(primary);
        app.Resources["AccentColor"]        = Color.Parse(accent);
        app.Resources["GradientStartColor"] = Color.Parse(gradientStart);
        app.Resources["GradientMidColor"]   = Color.Parse(gradientMid);
        app.Resources["GradientEndColor"]   = Color.Parse(gradientEnd);
        app.Resources["PrimaryBrush"]       = new SolidColorBrush(Color.Parse(primary));
        app.Resources["AccentBrush"]        = new SolidColorBrush(Color.Parse(accent));
    }

    private static string Coalesce(string name, string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            InstallerLog.Error($"BrandPalette: token '{name}' was null/empty in BrandTokens — using fallback '{fallback}'");
            return fallback;
        }
        return value;
    }
}
