using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Packaging;
using SigilBuild.Packaging.Msix;
using Xunit;

namespace SigilBuild.Packaging.Tests.Msix;

public class MsixPackagerTests
{
    [Fact]
    public async Task Pack_OnNonWindows_ReportsDiagnosticAndDoesNotProduceArtifact()
    {
        if (OperatingSystem.IsWindows()) return; // skip on Windows

        var manifest = new SigilManifest(
            "v1.0",
            new AppSection("com.example.App", "app", "1.0.0", "Example Inc.", null, null),
            new BuildSection("Fixtures/sample-source", null, null, true),
            new PackageSection(new[] { PackageFormat.Msix }, new[] { TargetArchitecture.X64 },
                new MsixOptions("CN=Example Inc.", null, null)),
            null, null, null, null, SourceLocation.Unknown);

        var outDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(outDir);
        try
        {
            var sut = new MsixPackager();
            var result = await sut.PackAsync(manifest,
                new PackOptions("Fixtures/sample-source", outDir, PackageFormat.Msix, TargetArchitecture.X64),
                CancellationToken.None);

            result.Artifact.Should().BeNull();
            result.Diagnostics.Should().NotBeEmpty();
        }
        finally { Directory.Delete(outDir, recursive: true); }
    }

    [Fact]
    public async Task Pack_OnWindows_ProducesMsixWhenSdkPresent()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!WindowsSdkLocator.TryLocateBin(out _)) return; // skip if SDK absent

        // App name "app" matches fixture's app.exe so AppxManifest Executable="app.exe" is valid.
        var fixtureDir = Path.Combine(
            Path.GetDirectoryName(typeof(MsixPackagerTests).Assembly.Location)!,
            "Fixtures", "sample-source");
        var manifest = new SigilManifest(
            "v1.0",
            new AppSection("com.example.App", "app", "1.0.0", "CN=Example Inc.", null, null),
            new BuildSection(fixtureDir, null, null, true),
            new PackageSection(new[] { PackageFormat.Msix }, new[] { TargetArchitecture.X64 },
                new MsixOptions("CN=Example Inc.", null, null)),
            null, null, null, null, SourceLocation.Unknown);

        var outDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(outDir);
        try
        {
            var sut = new MsixPackager();
            var result = await sut.PackAsync(manifest,
                new PackOptions(fixtureDir, outDir, PackageFormat.Msix, TargetArchitecture.X64),
                CancellationToken.None);

            result.Diagnostics.Should().BeEmpty();
            result.Artifact.Should().NotBeNull();
            File.Exists(result.Artifact!.Path).Should().BeTrue();
            result.Artifact.Path.Should().EndWith(".msix");
        }
        finally { Directory.Delete(outDir, recursive: true); }
    }

    // Locks docs/architecture/adr-msix-companion.md (option b): MSIX is a native OS
    // installer and never bundles a companion wizard. Even when the manifest declares
    // an `installer` section, the produced package must contain NO installer.exe and
    // NO BrandTokens.g.json sidecar.
    [Fact]
    public async Task Pack_WithInstallerSection_DoesNotBundleCompanionOrBrandTokens()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!WindowsSdkLocator.TryLocateBin(out _)) return; // skip if SDK absent

        var fixtureDir = Path.Combine(
            Path.GetDirectoryName(typeof(MsixPackagerTests).Assembly.Location)!,
            "Fixtures", "sample-source");
        var manifest = new SigilManifest(
            "v1.0",
            new AppSection("com.example.App", "app", "1.0.0", "CN=Example Inc.", null, null),
            new BuildSection(fixtureDir, null, null, true),
            new PackageSection(new[] { PackageFormat.Msix }, new[] { TargetArchitecture.X64 },
                new MsixOptions("CN=Example Inc.", null, null)),
            null, null, null,
            Installer: new InstallerSection(new InstallerBrand(null, null, "#1F2937", "#3B82F6")),
            Location: SourceLocation.Unknown);

        var outDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(outDir);
        try
        {
            var sut = new MsixPackager();
            var result = await sut.PackAsync(manifest,
                new PackOptions(fixtureDir, outDir, PackageFormat.Msix, TargetArchitecture.X64),
                CancellationToken.None);

            result.Diagnostics.Should().BeEmpty();
            result.Artifact.Should().NotBeNull();

            using var archive = ZipFile.OpenRead(result.Artifact!.Path);
            var names = archive.Entries.Select(e => Path.GetFileName(e.FullName)).ToList();
            names.Should().NotContain("installer.exe");
            names.Should().NotContain("BrandTokens.g.json");
        }
        finally { Directory.Delete(outDir, recursive: true); }
    }
}
