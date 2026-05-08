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

        // Output filename mirrors the Zip/Msix convention: id-version-arch tag.
        // Sanitize the user-controlled App.Name segment against path-traversal /
        // illegal characters so a malicious or accidental manifest cannot escape
        // the output directory.
        var archStr = options.Architecture.ToString().ToLowerInvariant();
        var safeName = SanitizeFileNameSegment(manifest.App.Name);
        var outputName = $"{safeName}-{manifest.App.Version}-{archStr}-Setup.exe";
        var outputPath = Path.Combine(options.OutputDirectory, outputName);

        Directory.CreateDirectory(options.OutputDirectory);
        if (File.Exists(outputPath))
            File.Delete(outputPath);
        File.Copy(stubPath, outputPath);

        // Embed step blob + payload — stubbed for Task 7; lands in Task 14.
        // (See WrapperResourceWriter.)
        ct.ThrowIfCancellationRequested();

        // Compute sha256 + size for the artifact descriptor.
        var sha256 = ManifestHasher.Sha256(outputPath);
        var size = new FileInfo(outputPath).Length;

        var artifact = new PackedArtifact(outputPath, sha256, size);
        return Task.FromResult(new PackResult(artifact, Array.Empty<Diagnostic>()));
    }

    private static string SanitizeFileNameSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
            return "app";

        var invalid = Path.GetInvalidFileNameChars();
        Span<char> buffer = stackalloc char[segment.Length];
        var i = 0;
        foreach (var c in segment)
        {
            // Reject path separators outright (they're in InvalidFileNameChars on Windows
            // but listed defensively for cross-platform clarity).
            if (c is '/' or '\\' or ':' || Array.IndexOf(invalid, c) >= 0)
                buffer[i++] = '_';
            else
                buffer[i++] = c;
        }
        var sanitized = new string(buffer[..i]).Trim('.', ' ');
        return string.IsNullOrEmpty(sanitized) ? "app" : sanitized;
    }
}
