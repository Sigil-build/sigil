using System;

namespace SigilBuild.Installer.BrandGenerator;

public static class WcagContrast
{
    public static double Ratio(string fgHex, string bgHex)
    {
        var l1 = RelativeLuminance(fgHex);
        var l2 = RelativeLuminance(bgHex);
        var (lighter, darker) = l1 > l2 ? (l1, l2) : (l2, l1);
        return (lighter + 0.05) / (darker + 0.05);
    }

    public static bool PassesAaAgainstWhite(string fgHex) =>
        Ratio(fgHex, "#FFFFFF") >= 4.5;

    /// <summary>
    /// WCAG-AA (4.5:1) contrast check for arbitrary foreground/background pairs.
    /// Used to validate the derived rail-muted text against the rail background
    /// (T7), in addition to the primary-vs-white check above.
    /// </summary>
    public static bool PassesAa(string fgHex, string bgHex) =>
        Ratio(fgHex, bgHex) >= 4.5;

    private static double RelativeLuminance(string hex)
    {
        var (r, g, b) = ParseRgb(hex);
        double R = Channel(r / 255.0);
        double G = Channel(g / 255.0);
        double B = Channel(b / 255.0);
        return 0.2126 * R + 0.7152 * G + 0.0722 * B;
    }

    private static double Channel(double v) =>
        v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);

    private static (int r, int g, int b) ParseRgb(string hex)
    {
        var s = hex.TrimStart('#');
        if (s.Length != 6) throw new ArgumentException($"expected #RRGGBB, got {hex}");
        return (
            Convert.ToInt32(s.Substring(0, 2), 16),
            Convert.ToInt32(s.Substring(2, 2), 16),
            Convert.ToInt32(s.Substring(4, 2), 16));
    }
}
