using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SigilBuild.Core.Manifest;
using SigilBuild.Packaging.Common;

namespace SigilBuild.Packaging.Zip;

public sealed class ZipPackager : IPackager
{
    // 1980-01-01 00:00:00 UTC — the earliest timestamp the ZIP format permits.
    // Fixed so every run of identical inputs produces byte-identical output.
    private static readonly DateTimeOffset DeterministicMtime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public PackageFormat Format => PackageFormat.Zip;

    public Task<PackResult> PackAsync(SigilManifest manifest, PackOptions options, CancellationToken ct)
    {
        Directory.CreateDirectory(options.OutputDirectory);
        var archStr = options.Architecture.ToString().ToLowerInvariant();
        var fileName = $"{manifest.App.Id}-{manifest.App.Version}-{archStr}.zip";
        var outPath = Path.Combine(options.OutputDirectory, fileName);

        var files = DeterministicFileWalker
            .Walk(options.SourceDirectory, manifest.Build.Include, manifest.Build.Exclude)
            .ToArray();

        if (File.Exists(outPath)) File.Delete(outPath);

        using (var fs = File.Create(outPath))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false))
        {
            // sigil-manifest.json first so it is easy to locate
            var manifestBytes = SigilManifestJsonWriter.Build(manifest, files);
            WriteEntry(zip, "sigil-manifest.json", manifestBytes);

            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                var bytes = File.ReadAllBytes(f.AbsolutePath);
                WriteEntry(zip, f.RelativePath, bytes);
            }
        }

        var sha = ManifestHasher.Sha256(outPath);
        var size = new FileInfo(outPath).Length;
        return Task.FromResult(new PackResult(
            new PackedArtifact(outPath, sha, size),
            Array.Empty<SigilBuild.Core.Diagnostics.Diagnostic>()));
    }

    private static void WriteEntry(ZipArchive zip, string name, byte[] payload)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = DeterministicMtime;
        using var entryStream = entry.Open();
        entryStream.Write(payload, 0, payload.Length);
    }
}
