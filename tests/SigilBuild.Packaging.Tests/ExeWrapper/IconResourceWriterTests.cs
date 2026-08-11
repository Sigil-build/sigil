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
    [RuntimeStagedFact]
    public async Task WriteAsync_ReplacesIconInWrapperExe()
    {
        // Reports a genuine Skipped result (via RuntimeStagedFactAttribute, register
        // row R6) when the Native AOT wrapper runtime isn't staged into
        // runtimes/win-x64/ (e.g. CI's build/test job, which does not run the
        // aot-publish job first). Mirrors ExeWrapperPackager_StampsIconOnProducedSetupExe
        // below; the leg runs wherever the runtime IS staged (locally, or a job
        // that stages it) and the wrapper-vm / aot-publish CI jobs are the arbiter.
        // RuntimeStagedFact already checked this exact path (WrapperRuntimeLocator.Locate
        // resolves the identical runtimes/win-x64/SigilBuild.Installer.Host.exe), so
        // Locate is called unguarded here: if it still throws, that is a real bug (a
        // race, or the locator and the attribute's check disagreeing) worth failing on,
        // not swallowing.
        var stubExe = WrapperRuntimeLocator.Locate(SigilBuild.Core.Manifest.TargetArchitecture.X64);
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

    [RuntimeStagedFact]
    public async Task ExeWrapperPackager_StampsIconOnProducedSetupExe_WhenWrapperRuntimeStaged()
    {
        // See WriteAsync_ReplacesIconInWrapperExe above: RuntimeStagedFact already
        // verified this exact path, so Locate is called unguarded.
        var stubExe = WrapperRuntimeLocator.Locate(SigilBuild.Core.Manifest.TargetArchitecture.X64);

        // Fixtures/minimal-payload is a checked-in fixture the csproj copies to the
        // test output directory on every build (CopyToOutputDirectory), so unlike the
        // runtime staging above it is not an environment precondition that legitimately
        // varies — if it's ever missing, that is a broken build/checkout, and letting
        // ManifestLoader.LoadAsync fail loudly below is more honest than quietly
        // returning would be (register row R6: the original `if (!Directory.Exists(...))
        // return;` here was removed rather than converted to a Skip attribute).
        var fixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-payload");

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

    [KioskSetupFact]
    public void Kiosk_Setup_HasEmbeddedUninstaller()
    {
        var setup = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..",
            "tests", "kiosk", "dist", "Embed.Infinity.Kiosk-1.0.0-x64-Setup.exe"));
        var bytes = ResourceReader.Read(setup, "SIGIL_UNINSTALLER_V1");
        bytes.Length.Should().BeGreaterThan(1_000_000,
            "the embedded uninstaller is a ~3.7 MB stamped wrapper copy");
    }
}
