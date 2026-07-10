using System;
using System.IO;
using System.IO.Compression;

namespace SigilBuild.Wrapper.Engine;

/// <summary>
/// Extracts the <c>SIGIL_PAYLOAD_V1</c> zip resource embedded by the
/// <c>ExeWrapperPackager</c> into a session-scoped temp directory. Install
/// steps that read from the payload (<c>file_copy from: "**"</c>,
/// <c>from: "subdir/**"</c>, etc.) resolve relative paths against this dir.
/// </summary>
/// <remarks>
/// <para>
/// Why not just embed the files individually: the payload can be hundreds of
/// MB (real applications), and we want one streamable zip rather than
/// hundreds of named Win32 resources. The cost is a one-time extract on
/// install start; the temp dir is removed in <see cref="Dispose"/>.
/// </para>
/// <para>
/// The extracted root becomes the install-time current working directory so
/// existing <c>file_copy</c> globs like <c>"**"</c> or <c>"payload/**"</c>
/// resolve as the manifest author intended. Sigil's earlier convention
/// (cwd = sandbox root) was implicit and undocumented; this class makes it
/// explicit and uniform.
/// </para>
/// </remarks>
internal sealed class PayloadExtractor : IDisposable
{
    private readonly string _root;
    private readonly string? _previousCwd;
    private bool _disposed;

    private PayloadExtractor(string root, string? previousCwd)
    {
        _root = root;
        _previousCwd = previousCwd;
    }

    /// <summary>Absolute path to the extracted payload root (set as cwd while alive).</summary>
    public string Root => _root;

    /// <summary>
    /// Read the embedded <c>SIGIL_PAYLOAD_V1</c> resource, extract it to
    /// <c>%TEMP%\sigil-payload-{guid}\</c>, and pin the running process's cwd
    /// to that directory for the lifetime of this object. Returns <c>null</c>
    /// when no payload is embedded (un-stamped runtime, smoke-test builds).
    /// </summary>
    public static PayloadExtractor? Prepare()
    {
        var payloadBytes = WrapperBlob.LoadPayloadBytes();
        if (payloadBytes.Length == 0)
        {
            WrapperLog.Info("PayloadExtractor: no SIGIL_PAYLOAD_V1 resource embedded — skipping extract");
            return null;
        }

        var root = Path.Combine(Path.GetTempPath(), "sigil-payload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        WrapperLog.Info($"PayloadExtractor: extracting {payloadBytes.Length:N0} bytes to {root}");

        try
        {
            var fileCount = 0;
            using (var ms = new MemoryStream(payloadBytes))
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Read))
            {
                foreach (var entry in zip.Entries)
                {
                    // Directory marker — empty name with trailing slash.
                    if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                    {
                        Directory.CreateDirectory(Path.Combine(root, entry.FullName));
                        continue;
                    }

                    var dest = Path.GetFullPath(Path.Combine(root, entry.FullName));
                    // zip-slip defence — refuse any entry whose decompressed
                    // path would escape the root.
                    if (!dest.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            $"payload contains a zip-slip entry: '{entry.FullName}'");
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    entry.ExtractToFile(dest, overwrite: true);
                    fileCount++;
                }
            }

            WrapperLog.Info($"PayloadExtractor: extracted {fileCount} files");

            string? previousCwd = null;
            try { previousCwd = Directory.GetCurrentDirectory(); } catch { /* ignore */ }
            Directory.SetCurrentDirectory(root);
            WrapperLog.Info($"PayloadExtractor: cwd set to {root}");

            return new PayloadExtractor(root, previousCwd);
        }
        catch (Exception ex)
        {
            WrapperLog.Error("PayloadExtractor.Prepare: extract failed", ex);
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
#pragma warning disable CA1031 // Best-effort cleanup; logger swallows logger failures, this swallows fs failures.
        if (_previousCwd is not null)
        {
            try { Directory.SetCurrentDirectory(_previousCwd); }
            catch (Exception ex) { WrapperLog.Error("PayloadExtractor: restore cwd failed", ex); }
        }
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
                WrapperLog.Info($"PayloadExtractor: cleaned up {_root}");
            }
        }
        catch (Exception ex)
        {
            WrapperLog.Error($"PayloadExtractor: cleanup of {_root} failed", ex);
        }
#pragma warning restore CA1031
    }
}
