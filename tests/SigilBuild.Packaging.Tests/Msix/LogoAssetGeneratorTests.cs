using System.IO;
using FluentAssertions;
using SigilBuild.Packaging.Msix;
using SkiaSharp;
using Xunit;

namespace SigilBuild.Packaging.Tests.Msix;

public class LogoAssetGeneratorTests
{
    private static string CreateMasterLogo(int side)
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".png");
        using var bmp = new SKBitmap(side, side);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.White);
        using var paint = new SKPaint { Color = SKColors.SteelBlue };
        canvas.DrawRect(0, 0, side, side, paint);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        using var fs = File.Create(path);
        data.SaveTo(fs);
        return path;
    }

    [Fact]
    public void Generate_ProducesAllRequiredTileSizes()
    {
        var master = CreateMasterLogo(512);
        var outDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(outDir);
        try
        {
            LogoAssetGenerator.Generate(master, outDir);

            File.Exists(Path.Combine(outDir, "Square44x44Logo.png")).Should().BeTrue();
            File.Exists(Path.Combine(outDir, "Square150x150Logo.png")).Should().BeTrue();
            File.Exists(Path.Combine(outDir, "Wide310x150Logo.png")).Should().BeTrue();
            File.Exists(Path.Combine(outDir, "StoreLogo.png")).Should().BeTrue();

            using var bmp = SKBitmap.Decode(Path.Combine(outDir, "Square44x44Logo.png"));
            bmp.Width.Should().Be(44);
            bmp.Height.Should().Be(44);
        }
        finally
        {
            File.Delete(master);
            Directory.Delete(outDir, recursive: true);
        }
    }
}
