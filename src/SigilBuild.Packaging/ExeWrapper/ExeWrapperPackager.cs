using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SigilBuild.Core.Diagnostics;
using SigilBuild.Core.Manifest;
using SigilBuild.Packaging.Common;

namespace SigilBuild.Packaging.ExeWrapper;

/// <summary>
/// Packs an application as a single self-extracting <c>.exe</c> wrapper —
/// the AOT-published <c>SigilBuild.Wrapper</c> runtime, stamped with a step
/// blob and the payload archive as Win32 resources (see ADR-008).
/// </summary>
/// <remarks>
/// Task 7 skeleton: the packager copies the stub runtime to
/// <c>{OutputDirectory}/{App.Name}-Setup.exe</c> and produces a
/// <see cref="PackedArtifact"/> describing it. Step-blob generation and
/// payload embedding are stubbed — see <see cref="WrapperResourceWriter"/> —
/// and land in Task 14.
/// </remarks>
public sealed class ExeWrapperPackager : IPackager
{
    public PackageFormat Format => PackageFormat.Exe;

    public Task<PackResult> PackAsync(SigilManifest manifest, PackOptions options, CancellationToken ct)
    {
        // Locate the AOT-published wrapper runtime.
        var stubPath = WrapperRuntimeLocator.Locate();

        // Output filename: <appName>-Setup.exe under options.OutputDirectory.
        var outputName = $"{manifest.App.Name}-Setup.exe";
        var outputPath = Path.Combine(options.OutputDirectory, outputName);

        Directory.CreateDirectory(options.OutputDirectory);
        File.Copy(stubPath, outputPath, overwrite: true);

        // Embed step blob + payload — stubbed for Task 7; lands in Task 14.
        // (See WrapperResourceWriter.)
        ct.ThrowIfCancellationRequested();

        // Compute sha256 + size for the artifact descriptor.
        var sha256 = ManifestHasher.Sha256(outputPath);
        var size = new FileInfo(outputPath).Length;

        var artifact = new PackedArtifact(outputPath, sha256, size);
        return Task.FromResult(new PackResult(artifact, Array.Empty<Diagnostic>()));
    }
}
