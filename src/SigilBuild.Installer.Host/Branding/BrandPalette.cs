using Avalonia;
using Avalonia.Media;

namespace SigilBuild.Installer.Host.Branding;

public static class BrandPalette
{
    public static void Apply(Application app, BrandTokens tokens)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(tokens);
        app.Resources["PrimaryColor"] = Color.Parse(tokens.PrimaryColor);
        app.Resources["AccentColor"] = Color.Parse(tokens.AccentColor);
        app.Resources["GradientStartColor"] = Color.Parse(tokens.GradientStart);
        app.Resources["GradientMidColor"] = Color.Parse(tokens.GradientMid);
        app.Resources["GradientEndColor"] = Color.Parse(tokens.GradientEnd);
        app.Resources["PrimaryBrush"] = new SolidColorBrush(Color.Parse(tokens.PrimaryColor));
        app.Resources["AccentBrush"] = new SolidColorBrush(Color.Parse(tokens.AccentColor));
    }
}
