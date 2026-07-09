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

    /// <summary>
    /// T4 acceptance: packing a manifest whose <c>package.formats</c> is
    /// <c>[exe]</c> produces one <c>&lt;App&gt;-&lt;ver&gt;-&lt;arch&gt;-Setup.exe</c>
    /// per declared architecture, each a valid PE carrying the stamped
    /// <c>SIGIL_BLOB_V1</c> + <c>SIGIL_PAYLOAD_V2</c> resources.
    /// <para>
    /// Gating: mirrors the existing skip-gated pack tests — the test skips when the
    /// AOT host runtime is not staged under <c>runtimes/win-x64/</c> (non-Windows,
    /// or a plain build that has not run <c>scripts/publish-installer-runtime.ps1</c>),
    /// so the normal <c>dotnet test</c> run never triggers the slow AOT publish.
    /// When the real x64 runtime <b>is</b> staged, the test additionally stages an
    /// arm64 stand-in (a copy of the x64 host — <c>BeginUpdateResourceW</c> works on
    /// any PE regardless of its target machine) to exercise the multi-arch path, then
    /// removes it. That stand-in is reached only inside the already-gated body, so the
    /// plain-build skip path is unaffected.
    /// </para>
    /// </summary>
    [Fact]
    public async Task PackAsync_produces_arch_tagged_Setup_exe_with_sigil_resources_per_architecture()
    {
        var runtime = LocateStagedRuntime();
        if (runtime is null)
        {
            Console.WriteLine(
                "SKIP: PackAsync_produces_arch_tagged_Setup_exe_with_sigil_resources_per_architecture — " +
                "runtimes/win-x64/SigilBuild.Installer.Host.exe not staged (non-Windows or AOT " +
                "runtime not published). Run scripts/publish-installer-runtime.ps1.");
            return;
        }

        var fixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-payload");
        var payloadDir = Path.Combine(fixtureDir, "payload");
        var outputDir = Path.Combine(Path.GetTempPath(), $"sigil-exe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);

        var load = await ManifestLoader.LoadAsync(
            Path.Combine(fixtureDir, "sigil.yaml"), new ProcessEnvironmentReader());
        load.Manifest.Should().NotBeNull();
        var manifest = load.Manifest!;

        // Stage an arm64 stand-in from the real x64 host so the multi-arch loop
        // (one Setup.exe per declared architecture) is exercised end-to-end.
        var arm64Dir = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-arm64");
        var arm64Stub = Path.Combine(arm64Dir, "SigilBuild.Installer.Host.exe");
        var stagedArm64 = false;
        if (!File.Exists(arm64Stub))
        {
            Directory.CreateDirectory(arm64Dir);
            File.Copy(runtime, arm64Stub);
            stagedArm64 = true;
        }

        try
        {
            var packager = new ExeWrapperPackager();
            foreach (var arch in new[] { TargetArchitecture.X64, TargetArchitecture.Arm64 })
            {
                var options = new PackOptions(
                    SourceDirectory: payloadDir,
                    OutputDirectory: outputDir,
                    Format: PackageFormat.Exe,
                    Architecture: arch);

                var result = await packager.PackAsync(manifest, options, CancellationToken.None);

                result.Artifact.Should().NotBeNull();
                var archTag = arch.ToString().ToLowerInvariant();
                Path.GetFileName(result.Artifact!.Path)
                    .Should().Be($"WrapApp-0.1.0-{archTag}-Setup.exe");
                File.Exists(result.Artifact.Path).Should().BeTrue();

                // Valid PE (LoadLibraryEx succeeds) carrying the stamped resources.
                ResourceReader.Read(result.Artifact.Path, "SIGIL_BLOB_V1")
                    .Should().NotBeEmpty("the JSON step/parameter blob is stamped as SIGIL_BLOB_V1");
                ResourceReader.Read(result.Artifact.Path, "SIGIL_PAYLOAD_V2")
                    .Should().NotBeEmpty("the fixture payload is stamped as SIGIL_PAYLOAD_V2");
            }
        }
        finally
        {
            if (stagedArm64)
            {
                try { Directory.Delete(arm64Dir, recursive: true); } catch (IOException) { }
            }
            try { Directory.Delete(outputDir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Format_property_is_Exe()
    {
        new ExeWrapperPackager().Format.Should().Be(PackageFormat.Exe);
    }
}
