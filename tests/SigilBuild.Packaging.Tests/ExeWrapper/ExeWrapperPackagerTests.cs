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
    /// <summary>
    /// Resolves the Native-AOT host runtime staged for this test session. The
    /// runtime is expected to be pre-staged under
    /// <c>runtimes/win-x64/SigilBuild.Installer.Host.exe</c> (next to the test
    /// assembly) by <c>scripts/publish-installer-runtime.ps1</c>. This test
    /// deliberately does NOT trigger an on-demand AOT publish — that keeps the
    /// normal <c>dotnet test</c> fast and free of the slow AOT link — so it skips
    /// gracefully when the runtime has not been staged.
    /// </summary>
    private static string? LocateStagedRuntime()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var staged = Path.Combine(
            AppContext.BaseDirectory, "runtimes", "win-x64", "SigilBuild.Installer.Host.exe");
        return File.Exists(staged) ? staged : null;
    }

    [Fact]
    public async Task PackAsync_emits_exe_under_5mb_overhead_on_top_of_payload()
    {
        var wrapperPath = LocateStagedRuntime();
        if (wrapperPath is null)
        {
            // AOT host runtime not staged for this session — skip gracefully.
            // Stage it with scripts/publish-installer-runtime.ps1 -DestinationRoot
            // <this test project's output dir> to exercise this path locally.
            Console.WriteLine(
                "SKIP: PackAsync_emits_exe_under_5mb_overhead_on_top_of_payload — " +
                "runtimes/win-x64/SigilBuild.Installer.Host.exe not staged (non-Windows " +
                "or AOT runtime not published). Run scripts/publish-installer-runtime.ps1.");
            return;
        }

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
