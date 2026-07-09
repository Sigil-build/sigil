using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace SigilBuild.Wrapper.Engine;

/// <summary>
/// Owns the temporary directory into which the embedded
/// <c>SIGIL_PAYLOAD_V1</c> archive is unpacked for the duration of a single
/// install run. <see cref="StepContext.ResolvePath"/> resolves
/// <c>payload://relative/path</c> sources against <see cref="Root"/>.
/// </summary>
/// <remarks>
/// The container is the deterministic Deflate zip that
/// <c>ExeWrapperPackager.BuildPayloadBytes</c> currently produces (the zstd
/// switch is a later task). Extraction is guarded against zip-slip: an entry
/// whose normalized path escapes <see cref="Root"/> aborts the whole extract.
/// <para>
/// <see cref="Dispose"/> removes the directory, so no <c>%TEMP%</c> state
/// leaks — the caller (<see cref="InstallSession"/>) disposes in a
/// <c>finally</c>, guaranteeing cleanup on success, step failure,
/// cancellation, and rollback alike.
/// </para>
/// </remarks>
internal sealed class PayloadExtraction : IDisposable
{
    /// <summary>Absolute path to the extracted payload's root directory.</summary>
    public string Root { get; }

    private PayloadExtraction(string root) => Root = root;

    /// <summary>
    /// Create a fresh <c>%TEMP%\sigil-&lt;appid&gt;-&lt;rand&gt;\</c> directory
    /// and unpack <paramref name="archiveBytes"/> into it. If extraction fails
    /// part-way the partial directory is removed before the exception
    /// propagates, so a failed extract never leaks temp state either.
    /// </summary>
    public static PayloadExtraction Extract(byte[] archiveBytes, string appId)
    {
        ArgumentNullException.ThrowIfNull(archiveBytes);
        ArgumentException.ThrowIfNullOrEmpty(appId);

        var root = CreateUniqueTempDir(appId);
        try
        {
            using var ms = new MemoryStream(archiveBytes, writable: false);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
            foreach (var entry in zip.Entries)
            {
                // Skip directory entries (trailing '/', empty Name).
                if (string.IsNullOrEmpty(entry.Name) ||
                    entry.FullName.EndsWith('/') ||
                    entry.FullName.EndsWith('\\'))
                {
                    continue;
                }

                var dest = ResolveEntryPath(root, entry.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

                using var entryStream = entry.Open();
                using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
                entryStream.CopyTo(fs);
            }
        }
        catch
        {
            TryDelete(root);
            throw;
        }

        return new PayloadExtraction(root);
    }

    /// <summary>Best-effort removal of the extracted payload directory.</summary>
    public void Dispose() => TryDelete(Root);

    private static string CreateUniqueTempDir(string appId)
    {
        var name = $"sigil-{SanitizeAppId(appId)}-{Path.GetRandomFileName()}";
        var dir = Path.Combine(Path.GetTempPath(), name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Reduce an app id (e.g. <c>com.acme.Studio</c>) to a short, filesystem-safe
    /// segment for the temp directory name.
    /// </summary>
    private static string SanitizeAppId(string appId)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(appId.Length);
        foreach (var c in appId)
        {
            sb.Append(Array.IndexOf(invalid, c) >= 0 || c is '/' or '\\' or ':' ? '_' : c);
        }
        var cleaned = sb.ToString().Trim('.', ' ', '_');
        return cleaned.Length == 0 ? "app" : cleaned;
    }

    /// <summary>
    /// Map a zip entry's relative path onto the extraction root, rejecting any
    /// entry whose normalized destination escapes the root (zip-slip defence).
    /// </summary>
    private static string ResolveEntryPath(string root, string entryName)
    {
        var rel = entryName
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        var rootFull = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(rootFull, rel));

        var rootPrefix = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"payload archive entry '{entryName}' escapes the extraction root");
        }

        return full;
    }

    private static void TryDelete(string dir)
    {
#pragma warning disable CA1031 // Best-effort temp cleanup; a leftover dir is harmless and must not mask the real result.
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // Best-effort.
        }
#pragma warning restore CA1031
    }
}
