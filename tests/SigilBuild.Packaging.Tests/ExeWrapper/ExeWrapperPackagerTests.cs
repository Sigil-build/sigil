using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Configuration;
using SigilBuild.Core.Manifest;
using SigilBuild.Packaging.ExeWrapper;
using Xunit;

namespace SigilBuild.Packaging.Tests.ExeWrapper;

public class ExeWrapperPackagerTests
{
    [Fact(Skip = "Re-enable once the AOT-published Wrapper.exe is available in runtimes/win-x64/. Tracked in Task 14.")]
    public async Task PackAsync_emits_exe_under_5mb_overhead_on_top_of_payload()
    {
        var fixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-payload");
        var outputDir  = Path.Combine(Path.GetTempPath(), $"sigil-wrap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);

        var loadResult = await ManifestLoader.LoadAsync(
            Path.Combine(fixtureDir, "sigil.yaml"),
            new ProcessEnvironmentReader());
        loadResult.Manifest.Should().NotBeNull();

        var packager = new ExeWrapperPackager();
        var options = new PackOptions(
            SourceDirectory: Path.Combine(fixtureDir, "payload"),
            OutputDirectory: outputDir,
            Format: PackageFormat.Exe,
            Architecture: TargetArchitecture.X64);

        var result = await packager.PackAsync(loadResult.Manifest!, options, CancellationToken.None);

        result.Artifact.Should().NotBeNull();
        File.Exists(result.Artifact!.Path).Should().BeTrue();

        var payloadSize = new DirectoryInfo(Path.Combine(fixtureDir, "payload"))
            .EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        var overheadBytes = result.Artifact.SizeBytes - payloadSize;
        overheadBytes.Should().BeLessThan(5L * 1024 * 1024,
            "wrapper-runtime overhead is hard-capped at 5 MB per ADR-008");
    }

    [Fact]
    public void Format_property_is_Exe()
    {
        new ExeWrapperPackager().Format.Should().Be(PackageFormat.Exe);
    }
}
