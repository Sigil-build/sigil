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

            // Decode from bytes, not from path: SKBitmap.Decode(string) opens a
            // native file stream whose handle release is not guaranteed to be
            // synchronous with the `using` on the returned SKBitmap (it can
            // depend on GC/finalizer timing), which intermittently raced with
            // the recursive Directory.Delete below under parallel test load
            // (IOException: "Square44x44Logo.png" in use). Reading the bytes
            // first — the same fix LogoAssetGenerator.Generate itself already
            // applies to the master logo, see its comment — closes the file
            // before any decode happens, so no handle can outlive this method.
            using var bmp = SKBitmap.Decode(File.ReadAllBytes(Path.Combine(outDir, "Square44x44Logo.png")));
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
