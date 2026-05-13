using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using SigilBuild.Core.Diagnostics;
using SigilBuild.Core.Manifest;
using SigilBuild.Packaging.Common;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Json;

namespace SigilBuild.Packaging.ExeWrapper;

/// <summary>
/// Packs an application as a single self-extracting <c>.exe</c> wrapper —
/// the AOT-published <c>SigilBuild.Wrapper</c> runtime, stamped with a step
/// blob and the payload archive as Win32 resources (see ADR-008).
/// </summary>
/// <remarks>
/// Task 14 implementation: the packager copies the stub runtime to
/// <c>{OutputDirectory}/{App.Name}-Setup.exe</c>, then embeds the JSON
/// step + parameter blob (<c>SIGIL_BLOB_V1</c>) and a zip archive of the
/// source directory (<c>SIGIL_PAYLOAD_V1</c>) as Win32 <c>RT_RCDATA</c>
/// resources via <see cref="WrapperResourceWriter"/>.
/// </remarks>
public sealed class ExeWrapperPackager : IPackager
{
    public PackageFormat Format => PackageFormat.Exe;

    public async Task<PackResult> PackAsync(SigilManifest manifest, PackOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(options);

        // Locate the AOT-published wrapper runtime. Surface the missing-runtime
        // case as a SIG0120 diagnostic — the dev workflow expects build-wrappers
        // to stage the AOT exe alongside the SDK before pack-time.
        string stubPath;
        try
        {
            stubPath = WrapperRuntimeLocator.Locate();
        }
        catch (FileNotFoundException ex)
        {
            return new PackResult(null, new[]
            {
                new Diagnostic(DiagnosticSeverity.Error, "SIG0120",
                    $"EXE-wrapper packaging requires the AOT-published SigilBuild.Wrapper runtime. {ex.Message}",
                    SourceLocation.Unknown,
                    "https://docs.sigil.build/diagnostics/SIG0120"),
            });
        }

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

        ct.ThrowIfCancellationRequested();

        // Build the SIGIL_BLOB_V1 wire payload: serialize a
        // SerializableWrapperBlob via the source-generated context shared
        // with the wrapper runtime.
        var blobBytes = BuildBlobBytes(manifest);

        // Build the SIGIL_PAYLOAD_V1 wire payload: a zip archive of the
        // source directory. Richer payload-extraction (zstd, splitting,
        // payload:// path resolution) lands with Tasks 15+.
        var payloadBytes = BuildPayloadBytes(options.SourceDirectory, ct);

        // Embed both resources via the Win32 update-resource flow.
        await WrapperResourceWriter.WriteAsync(outputPath, blobBytes, payloadBytes, ct)
            .ConfigureAwait(false);

        // Compute sha256 + size *after* the resource embed so the artifact
        // descriptor reflects the final on-disk shape.
        var sha256 = ManifestHasher.Sha256(outputPath);
        var size = new FileInfo(outputPath).Length;

        var artifact = new PackedArtifact(outputPath, sha256, size);
        return new PackResult(artifact, Array.Empty<Diagnostic>());
    }

    private static byte[] BuildBlobBytes(SigilManifest manifest)
    {
        var parameters = manifest.Parameters is null
            ? Array.Empty<ParameterDefinition>()
            : ParametersToList(manifest.Parameters);

        var inMemory = new WrapperBlob(
            AppId: manifest.App.Id,
            Parameters: parameters,
            InstallSteps: manifest.InstallSteps ?? Array.Empty<InstallStep>(),
            PreInstall: manifest.PreInstall ?? Array.Empty<InstallStep>(),
            PostInstall: manifest.PostInstall ?? Array.Empty<InstallStep>(),
            // Update-step block doesn't yet exist on SigilManifest (Task 19+);
            // emit an empty list for forward compatibility.
            UpdateSteps: Array.Empty<InstallStep>());

        var serializable = SerializableWrapperBlob.FromWrapperBlob(inMemory);
        var json = System.Text.Json.JsonSerializer.Serialize(
            serializable, WrapperBlobJsonContext.Default.SerializableWrapperBlob);
        return System.Text.Encoding.UTF8.GetBytes(json);
    }

    private static ParameterDefinition[] ParametersToList(
        IReadOnlyDictionary<string, ParameterDefinition> map)
    {
        if (map.Count == 0) return Array.Empty<ParameterDefinition>();
        var arr = new ParameterDefinition[map.Count];
        var i = 0;
        foreach (var kv in map)
        {
            arr[i++] = kv.Value;
        }
        return arr;
    }

    private static byte[] BuildPayloadBytes(string sourceDirectory, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(sourceDirectory) || !Directory.Exists(sourceDirectory))
        {
            return Array.Empty<byte>();
        }

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var root = Path.GetFullPath(sourceDirectory);
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                var entry = zip.CreateEntry(rel, CompressionLevel.Optimal);
                entry.LastWriteTime = DeterministicMtime;
                using var entryStream = entry.Open();
                using var fs = File.OpenRead(file);
                fs.CopyTo(entryStream);
            }
        }
        return ms.ToArray();
    }

    // 1980-01-01 00:00:00 UTC — earliest timestamp the ZIP format permits.
    // Fixed for deterministic byte-identical output across builds.
    private static readonly DateTimeOffset DeterministicMtime =
        new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

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
