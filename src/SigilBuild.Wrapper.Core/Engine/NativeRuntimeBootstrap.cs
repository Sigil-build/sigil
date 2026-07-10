using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace SigilBuild.Wrapper.Engine;

/// <summary>
/// Makes a standalone stamped <c>Setup.exe</c> self-contained for the GUI
/// wizard (spec T18). A wizard installer needs its Skia / ANGLE / HarfBuzz
/// native DLLs (~18 MB) that Native AOT publishes <em>beside</em> the exe, not
/// inside it. The packager archives that native-dep set into the
/// <c>SIGIL_RUNTIME_V1</c> Win32 resource; before the Avalonia GUI starts the
/// host calls <see cref="EnsureNativeDependenciesLoadable"/>, which extracts the
/// DLLs to a per-user cache directory and adds that directory to the process's
/// native DLL search path so SkiaSharp/Avalonia native P/Invokes resolve.
/// </summary>
/// <remarks>
/// <para>
/// This type is <b>public</b> on purpose: the host assembly's output name is
/// <c>installer</c>, so the engine's
/// <c>InternalsVisibleTo("SigilBuild.Installer.Host")</c> is inert and the host
/// can only reach the engine through its public surface.
/// </para>
/// <para>
/// Only the GUI path calls this. The <c>/silent</c> / <c>/verysilent</c> and
/// non-interactive (uninstall) paths run headless — they never initialise Skia
/// — so they skip the bootstrap entirely. An un-stamped dev run has no
/// <c>SIGIL_RUNTIME_V1</c> resource and its native DLLs already sit beside the
/// exe, so the bootstrap is a no-op there.
/// </para>
/// </remarks>
public static partial class NativeRuntimeBootstrap
{
    /// <summary>
    /// Win32 <c>RT_RCDATA</c> resource name carrying the deterministic zip of the
    /// host's native dependencies. Shared with <c>WrapperResourceWriter</c> (which
    /// writes it) and <see cref="WrapperBlob.LoadRuntimeBytes"/> (which reads it).
    /// </summary>
    public const string RuntimeResourceName = "SIGIL_RUNTIME_V1";

    /// <summary>Marker written after a complete extraction; gates idempotent re-runs.</summary>
    private const string CompletionMarkerName = ".sigil-runtime-complete";

    // LOAD_LIBRARY_SEARCH_DEFAULT_DIRS = APPLICATION_DIR | SYSTEM32 | USER_DIRS.
    private const uint LoadLibrarySearchDefaultDirs = 0x00001000;

    /// <summary>
    /// GUI-path bootstrap. If the running exe carries a
    /// <see cref="RuntimeResourceName"/> resource (a stamped <c>Setup.exe</c>),
    /// extract the embedded native DLLs to a per-user cache directory and add that
    /// directory to the native DLL search path. Idempotent: a second call — or a
    /// re-run of the same stamped exe — reuses the already-extracted directory and
    /// never duplicates work. Returns the extraction directory, or <c>null</c>
    /// when no resource is present (an un-stamped dev run whose native DLLs already
    /// sit beside the exe, so nothing to do).
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static string? EnsureNativeDependenciesLoadable()
    {
        var archive = WrapperBlob.LoadRuntimeBytes();
        if (archive is null || archive.Length == 0)
        {
            return null;
        }

        var targetDir = ResolveCacheDirectory(archive);
        ExtractIfNeeded(archive, targetDir);
        AddNativeSearchDirectory(targetDir);
        return targetDir;
    }

    /// <summary>
    /// Resolve the stable per-user cache directory for an archive: keyed by the
    /// archive's SHA-256 so identical native-dep sets share one extraction and a
    /// version bump lands in a fresh directory.
    /// </summary>
    internal static string ResolveCacheDirectory(byte[] archiveBytes)
    {
        var hash = Convert.ToHexString(SHA256.HashData(archiveBytes)).ToLowerInvariant();
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(baseDir, "Sigil", "runtime", hash);
    }

    /// <summary>
    /// Extract <paramref name="archiveBytes"/> into <paramref name="targetDir"/>
    /// unless a completion marker shows a prior run already finished. Extraction
    /// itself is idempotent (skips files already present at the expected length),
    /// so a partially-extracted directory from an interrupted run self-heals.
    /// </summary>
    private static void ExtractIfNeeded(byte[] archiveBytes, string targetDir)
    {
        var marker = Path.Combine(targetDir, CompletionMarkerName);
        if (File.Exists(marker))
        {
            return;
        }

        ExtractArchive(archiveBytes, targetDir);
        File.WriteAllBytes(marker, Array.Empty<byte>());
    }

    /// <summary>
    /// Extract the deterministic native-dep zip (<see cref="RuntimeResourceName"/>)
    /// into <paramref name="targetDirectory"/> and return the extracted file paths,
    /// sorted. Entries are flat file names (the packager stores no directories), so
    /// every DLL lands directly in the target search directory. Idempotent: an
    /// existing file of the archived length is left untouched (so a DLL already
    /// loaded by a concurrent process is never clobbered). Guards against zip-slip.
    /// </summary>
    public static IReadOnlyList<string> ExtractArchive(byte[] archiveBytes, string targetDirectory)
    {
        ArgumentNullException.ThrowIfNull(archiveBytes);
        ArgumentException.ThrowIfNullOrEmpty(targetDirectory);

        Directory.CreateDirectory(targetDirectory);

        var extracted = new List<string>();
        using var ms = new MemoryStream(archiveBytes, writable: false);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        foreach (var entry in zip.Entries)
        {
            // Skip directory entries (trailing separator, empty Name).
            if (string.IsNullOrEmpty(entry.Name) ||
                entry.FullName.EndsWith('/') ||
                entry.FullName.EndsWith('\\'))
            {
                continue;
            }

            var dest = ResolveEntryPath(targetDirectory, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

            var info = new FileInfo(dest);
            if (!(info.Exists && info.Length == entry.Length))
            {
                using var entryStream = entry.Open();
                using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.Read);
                entryStream.CopyTo(fs);
            }

            extracted.Add(dest);
        }

        extracted.Sort(StringComparer.OrdinalIgnoreCase);
        return extracted;
    }

    /// <summary>
    /// Add <paramref name="directory"/> to the process's native DLL search path so
    /// subsequent <c>DllImport</c>/<c>LoadLibrary</c> resolution (SkiaSharp, ANGLE,
    /// HarfBuzz) finds the extracted DLLs there. Uses the modern, safe search
    /// mechanism: <c>SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS)</c>
    /// establishes the process default set (application dir + System32 + user
    /// dirs) and <c>AddDllDirectory</c> registers <paramref name="directory"/> as a
    /// user dir. Idempotent — re-adding the same directory is harmless.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static void AddNativeSearchDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);

        // Establish the process-wide safe default search set so that plain
        // DllImport resolution consults AddDllDirectory-registered directories.
        _ = SetDefaultDllDirectories(LoadLibrarySearchDefaultDirs);

        var cookie = AddDllDirectory(directory);
        if (cookie == IntPtr.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"AddDllDirectory failed for '{directory}'");
        }
    }

    /// <summary>
    /// Map a zip entry's relative path onto the extraction root, rejecting any
    /// entry whose normalized destination escapes the root (zip-slip defence).
    /// Mirrors <see cref="PayloadExtraction"/>'s guard.
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
                $"native-runtime archive entry '{entryName}' escapes the extraction root");
        }

        return full;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetDefaultDllDirectories(uint directoryFlags);

    [LibraryImport("kernel32.dll", EntryPoint = "AddDllDirectory",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr AddDllDirectory(string newDirectory);
}
