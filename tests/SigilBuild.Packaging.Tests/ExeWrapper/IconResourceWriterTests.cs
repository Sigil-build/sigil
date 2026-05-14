using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Packaging;
using SigilBuild.Packaging.ExeWrapper;
using Xunit;

namespace SigilBuild.Packaging.Tests.ExeWrapper;

public class IconResourceWriterTests
{
    [Fact(Skip = "Requires the AOT-published Wrapper.exe in runtimes/win-x64/.")]
    public async Task WriteAsync_ReplacesIconInWrapperExe()
    {
        var stubExe = WrapperRuntimeLocator.Locate();
        var tmp = Path.Combine(Path.GetTempPath(), $"sigil-icon-{Guid.NewGuid():N}.exe");
        File.Copy(stubExe, tmp, overwrite: true);
        try
        {
            var asm = typeof(WrapperResourceWriter).Assembly;
            await using var iconStream = asm.GetManifestResourceStream("SigilBuild.Packaging.DefaultInstallerIcon.ico")!;
            using var ms = new MemoryStream();
            await iconStream.CopyToAsync(ms);

            await IconResourceWriter.WriteAsync(tmp, ms.ToArray(), CancellationToken.None);

            var resourceBytes = ResourceReader.ReadIconGroup(tmp, "MAINICON");
            resourceBytes.Length.Should().BeGreaterThan(6, "RT_GROUP_ICON header is at least 6 bytes (ICONDIR)");
        }
        finally
        {
            try { File.Delete(tmp); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task ExeWrapperPackager_StampsIconOnProducedSetupExe_WhenWrapperRuntimeStaged()
    {
        string stubExe;
        try { stubExe = WrapperRuntimeLocator.Locate(); }
        catch (FileNotFoundException) { return; /* soft-skip — AOT runtime not staged */ }

        var fixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-payload");
        if (!Directory.Exists(fixtureDir)) return; // soft-skip — fixture not staged

        var loadResult = await SigilBuild.Core.Configuration.ManifestLoader.LoadAsync(
            Path.Combine(fixtureDir, "sigil.yaml"),
            new SigilBuild.Core.Configuration.ProcessEnvironmentReader());
        loadResult.Manifest.Should().NotBeNull();

        var outputDir = Path.Combine(Path.GetTempPath(), $"sigil-icon-pack-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);
        try
        {
            var packager = new ExeWrapperPackager();
            var options = new PackOptions(
                SourceDirectory: Path.Combine(fixtureDir, "payload"),
                OutputDirectory: outputDir,
                Format: SigilBuild.Core.Manifest.PackageFormat.Exe,
                Architecture: SigilBuild.Core.Manifest.TargetArchitecture.X64);
            var result = await packager.PackAsync(loadResult.Manifest!, options, CancellationToken.None);
            result.Artifact.Should().NotBeNull();

            var iconGroup = ResourceReader.ReadIconGroup(result.Artifact!.Path, "MAINICON");
            iconGroup.Length.Should().BeGreaterThan(6, "the bundled default icon should be stamped automatically");
        }
        finally
        {
            try { Directory.Delete(outputDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void Kiosk_Setup_HasEmbeddedUninstaller()
    {
        var setup = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..",
            "tests", "kiosk", "dist", "Embed.Infinity.Kiosk-1.0.0-x64-Setup.exe"));
        if (!File.Exists(setup)) return; // soft-skip
        var bytes = ResourceReader.Read(setup, "SIGIL_UNINSTALLER_V1");
        bytes.Length.Should().BeGreaterThan(1_000_000,
            "the embedded uninstaller is a ~3.7 MB stamped wrapper copy");
    }
}
