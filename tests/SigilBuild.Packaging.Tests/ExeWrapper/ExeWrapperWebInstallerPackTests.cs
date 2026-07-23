using System;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Configuration;
using SigilBuild.Core.Manifest;
using SigilBuild.Packaging.ExeWrapper;
using SigilBuild.Wrapper.Json;
using Xunit;

namespace SigilBuild.Packaging.Tests.ExeWrapper;

/// <summary>
/// P12 / T12.5: <c>pack --format exe --payload web --package-url URL</c> at the
/// full <see cref="ExeWrapperPackager.PackAsync"/> level — two artifacts, the
/// stub's embedded sha256 matches the actually-emitted full package, the stub
/// carries no <c>SIGIL_PAYLOAD_V2</c> resource, and two web packs of the same
/// input are byte-identical. Gated exactly like the existing exe-pack tests
/// (<see cref="ExeWrapperPackagerTests"/>): the Native-AOT host runtime must be
/// staged under <c>runtimes/win-x64/</c> (via
/// <c>scripts/publish-installer-runtime.ps1</c>) or these tests SKIP rather than
/// trigger a slow on-demand AOT publish. The live download-and-run of the stub
/// is CI-VM-only (T12.6) — not exercised here.
/// </summary>
public class ExeWrapperWebInstallerPackTests
{
    private const string PackageUrl = "https://cdn.example.com/acme/pkg.exe";

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

    private static SerializableWrapperBlob DeserializeBlob(string exePath)
    {
        var bytes = ResourceReader.Read(exePath, "SIGIL_BLOB_V1");
        return JsonSerializer.Deserialize(
            Encoding.UTF8.GetString(bytes), WrapperBlobJsonContext.Default.SerializableWrapperBlob)!;
    }

    [Fact]
    public async Task PackAsync_payload_web_emits_full_package_and_a_no_payload_stub_whose_blob_matches()
    {
        var runtime = LocateStagedRuntime();
        if (runtime is null)
        {
            Console.WriteLine(
                "SKIP: PackAsync_payload_web_emits_full_package_and_a_no_payload_stub_whose_blob_matches — " +
                "runtimes/win-x64/SigilBuild.Installer.Host.exe not staged (non-Windows or AOT runtime not " +
                "published). Run scripts/publish-installer-runtime.ps1.");
            return;
        }

        var fixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-payload");
        var outputDir = Path.Combine(Path.GetTempPath(), $"sigil-web-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);

        var load = await ManifestLoader.LoadAsync(
            Path.Combine(fixtureDir, "sigil.yaml"), new ProcessEnvironmentReader());
        load.Manifest.Should().NotBeNull();

        try
        {
            var packager = new ExeWrapperPackager();
            var options = new PackOptions(
                SourceDirectory: Path.Combine(fixtureDir, "payload"),
                OutputDirectory: outputDir,
                Format: PackageFormat.Exe,
                Architecture: TargetArchitecture.X64,
                Payload: PayloadMode.Web,
                PackageUrl: PackageUrl);

            var result = await packager.PackAsync(load.Manifest!, options, CancellationToken.None);

            // Artifact #1: the full package, built exactly as --payload embedded.
            result.Artifact.Should().NotBeNull();
            Path.GetFileName(result.Artifact!.Path).Should().Be("WrapApp-0.1.0-x64-Setup.exe");
            File.Exists(result.Artifact.Path).Should().BeTrue();
            ResourceReader.Read(result.Artifact.Path, "SIGIL_PAYLOAD_V2")
                .Should().NotBeEmpty("the full package embeds the real app payload");

            // Artifact #2: the small stub.
            result.SecondaryArtifact.Should().NotBeNull("--payload web must emit a second, stub artifact");
            var stub = result.SecondaryArtifact!;
            Path.GetFileName(stub.Path).Should().Be("WrapApp-0.1.0-x64-WebSetup.exe",
                "the stub's file name must clearly distinguish it from the full package");
            File.Exists(stub.Path).Should().BeTrue();

            // The stub carries NO app payload at all.
            Action readPayload = () => ResourceReader.Read(stub.Path, "SIGIL_PAYLOAD_V2");
            readPayload.Should().Throw<Win32Exception>("an empty payload means SIGIL_PAYLOAD_V2 is never written");

            // The stub's blob: exactly the download+run pair, sha256 == the full package's ACTUAL sha256.
            var stubBlob = DeserializeBlob(stub.Path);
            stubBlob.InstallSteps.Should().HaveCount(2);
            var download = stubBlob.InstallSteps.Should().ContainSingle(s => s.Type == "http_download").Subject;
            download.HttpUrl.Should().Be(PackageUrl);
            download.Sha256.Should().Be(result.Artifact.Sha256,
                "the stub must verify against the sha256 of the package ACTUALLY emitted, not a guess");
            stubBlob.InstallSteps.Should().ContainSingle(s => s.Type == "run_program");
        }
        finally
        {
            try { Directory.Delete(outputDir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task PackAsync_payload_web_is_deterministic_across_two_identical_packs()
    {
        var runtime = LocateStagedRuntime();
        if (runtime is null)
        {
            Console.WriteLine(
                "SKIP: PackAsync_payload_web_is_deterministic_across_two_identical_packs — " +
                "runtimes/win-x64/SigilBuild.Installer.Host.exe not staged (non-Windows or AOT runtime not " +
                "published). Run scripts/publish-installer-runtime.ps1.");
            return;
        }

        var fixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-payload");
        var outDir1 = Path.Combine(Path.GetTempPath(), $"sigil-web-a-{Guid.NewGuid():N}");
        var outDir2 = Path.Combine(Path.GetTempPath(), $"sigil-web-b-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outDir1);
        Directory.CreateDirectory(outDir2);

        var load = await ManifestLoader.LoadAsync(
            Path.Combine(fixtureDir, "sigil.yaml"), new ProcessEnvironmentReader());
        load.Manifest.Should().NotBeNull();

        try
        {
            var packager = new ExeWrapperPackager();
            var payloadDir = Path.Combine(fixtureDir, "payload");

            var r1 = await packager.PackAsync(load.Manifest!,
                new PackOptions(payloadDir, outDir1, PackageFormat.Exe, TargetArchitecture.X64, PayloadMode.Web, PackageUrl),
                CancellationToken.None);
            var r2 = await packager.PackAsync(load.Manifest!,
                new PackOptions(payloadDir, outDir2, PackageFormat.Exe, TargetArchitecture.X64, PayloadMode.Web, PackageUrl),
                CancellationToken.None);

            r1.SecondaryArtifact.Should().NotBeNull();
            r2.SecondaryArtifact.Should().NotBeNull();

            File.ReadAllBytes(r1.SecondaryArtifact!.Path).Should().Equal(
                File.ReadAllBytes(r2.SecondaryArtifact!.Path),
                "two --payload web packs of the same manifest + URL must produce a byte-identical stub");
            r1.SecondaryArtifact.Sha256.Should().Be(r2.SecondaryArtifact.Sha256);

            // The full packages backing them are byte-identical too (unchanged determinism).
            File.ReadAllBytes(r1.Artifact!.Path).Should().Equal(File.ReadAllBytes(r2.Artifact!.Path));
        }
        finally
        {
            try { Directory.Delete(outDir1, recursive: true); } catch (IOException) { }
            try { Directory.Delete(outDir2, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task PackAsync_payload_embedded_default_is_unchanged()
    {
        var runtime = LocateStagedRuntime();
        if (runtime is null)
        {
            Console.WriteLine(
                "SKIP: PackAsync_payload_embedded_default_is_unchanged — " +
                "runtimes/win-x64/SigilBuild.Installer.Host.exe not staged (non-Windows or AOT runtime not " +
                "published). Run scripts/publish-installer-runtime.ps1.");
            return;
        }

        var fixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-payload");
        var outputDir = Path.Combine(Path.GetTempPath(), $"sigil-embedded-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);

        var load = await ManifestLoader.LoadAsync(
            Path.Combine(fixtureDir, "sigil.yaml"), new ProcessEnvironmentReader());
        load.Manifest.Should().NotBeNull();

        try
        {
            var packager = new ExeWrapperPackager();
            // Default PayloadMode (no Payload/PackageUrl args) mirrors the pre-T12.5 call shape.
            var options = new PackOptions(
                Path.Combine(fixtureDir, "payload"), outputDir, PackageFormat.Exe, TargetArchitecture.X64);

            var result = await packager.PackAsync(load.Manifest!, options, CancellationToken.None);

            result.Artifact.Should().NotBeNull();
            result.SecondaryArtifact.Should().BeNull("--payload embedded (the default) emits only one artifact");
            ResourceReader.Read(result.Artifact!.Path, "SIGIL_PAYLOAD_V2").Should().NotBeEmpty();
        }
        finally
        {
            try { Directory.Delete(outputDir, recursive: true); } catch (IOException) { }
        }
    }
}
