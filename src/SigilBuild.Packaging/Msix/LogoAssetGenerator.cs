using System.IO;
using SkiaSharp;

namespace SigilBuild.Packaging.Msix;

public static class LogoAssetGenerator
{
    private static readonly (string FileName, int Width, int Height)[] Tiles =
    [
        ("Square44x44Logo.png", 44, 44),
        ("Square150x150Logo.png", 150, 150),
        ("Wide310x150Logo.png", 310, 150),
        ("StoreLogo.png", 50, 50),
    ];

    public static void Generate(string masterLogoPath, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        using var master = SKBitmap.Decode(masterLogoPath)
            ?? throw new IOException($"unable to decode '{masterLogoPath}'");

        foreach (var (name, w, h) in Tiles)
        {
            using var resized = master.Resize(new SKImageInfo(w, h), SKSamplingOptions.Default);
            if (resized is null) throw new IOException($"resize failed for {name}");
            using var img = SKImage.FromBitmap(resized);
            using var encodedData = img.Encode(SKEncodedImageFormat.Png, 100);
            var outPath = Path.Combine(outputDirectory, name);
            using var fs = File.Create(outPath);
            encodedData.SaveTo(fs);
        }
    }
}
