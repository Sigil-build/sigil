using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Packaging;
using SigilBuild.Packaging.Zip;
using Xunit;

namespace SigilBuild.Packaging.Tests.Zip;

public class ZipPackagerTests
{
    private static readonly string Source = Path.Combine("Fixtures", "sample-source");

    private static SigilManifest BuildManifest(IReadOnlyList<string>? include = null, IReadOnlyList<string>? exclude = null) =>
        new(
            Spec: "v1.0",
            App: new AppSection("com.example.App", "Example", "1.0.0", "Example Inc.", null, null),
            Build: new BuildSection(Source, include, exclude, Deterministic: true),
            Package: null, Sign: null, Publish: null, Updates: null, Installer: null,
            Location: SourceLocation.Unknown);

    [Fact]
    public async Task Pack_ProducesZipWithSigilManifestJson()
    {
        var outDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(outDir);
        try
        {
            var sut = new ZipPackager();
            var result = await sut.PackAsync(
                BuildManifest(),
                new PackOptions(Source, outDir, PackageFormat.Zip, TargetArchitecture.X64),
                CancellationToken.None);

            result.Diagnostics.Should().BeEmpty();
            result.Artifact.Should().NotBeNull();
            File.Exists(result.Artifact!.Path).Should().BeTrue();

            using var zip = ZipFile.OpenRead(result.Artifact.Path);
            zip.Entries.Should().Contain(e => e.FullName == "sigil-manifest.json");
            zip.Entries.Should().Contain(e => e.FullName == "app.exe");
        }
        finally { Directory.Delete(outDir, recursive: true); }
    }

    [Fact]
    public async Task Pack_TwoIdenticalRuns_ProduceByteIdenticalArchives()
    {
        var dir1 = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var dir2 = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir1);
        Directory.CreateDirectory(dir2);
        try
        {
            var sut = new ZipPackager();
            var manifest = BuildManifest();
            var r1 = await sut.PackAsync(manifest, new PackOptions(Source, dir1, PackageFormat.Zip, TargetArchitecture.X64), CancellationToken.None);
            await Task.Delay(50); // ensure mtime differs if non-deterministic
            var r2 = await sut.PackAsync(manifest, new PackOptions(Source, dir2, PackageFormat.Zip, TargetArchitecture.X64), CancellationToken.None);

            var bytes1 = File.ReadAllBytes(r1.Artifact!.Path);
            var bytes2 = File.ReadAllBytes(r2.Artifact!.Path);
            bytes1.Should().Equal(bytes2, "deterministic mode must produce byte-identical zips");
            r1.Artifact.Sha256.Should().Be(r2.Artifact.Sha256);
        }
        finally
        {
            Directory.Delete(dir1, recursive: true);
            Directory.Delete(dir2, recursive: true);
        }
    }

    [Fact]
    public async Task Pack_RespectsExcludeGlob()
    {
        var outDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(outDir);
        try
        {
            var sut = new ZipPackager();
            var result = await sut.PackAsync(
                BuildManifest(exclude: new[] { "**/*.pdb" }),
                new PackOptions(Source, outDir, PackageFormat.Zip, TargetArchitecture.X64),
                CancellationToken.None);

            using var zip = ZipFile.OpenRead(result.Artifact!.Path);
            zip.Entries.Select(e => e.FullName).Should().NotContain("debug/app.pdb");
        }
        finally { Directory.Delete(outDir, recursive: true); }
    }
}
