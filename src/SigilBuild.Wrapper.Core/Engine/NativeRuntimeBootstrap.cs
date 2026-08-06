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
/// DLLs to a content-keyed cache directory — under an administrator-only root when
/// the process is elevated — verifies every file in it against the embedded archive,
/// and only then adds that directory to the process's native DLL search path so
/// SkiaSharp/Avalonia native P/Invokes resolve.
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

    /// <summary>
    /// Marker written after a complete <em>and verified</em> extraction. It is a
    /// fast-path hint, never the trust decision: register row R4 is precisely that a
    /// marker an attacker can <c>touch</c> was treated as proof that the directory
    /// beside it held our bytes.
    /// </summary>
    private const string CompletionMarkerName = ".sigil-runtime-complete";

    // LOAD_LIBRARY_SEARCH_DEFAULT_DIRS = APPLICATION_DIR | SYSTEM32 | USER_DIRS.
    private const uint LoadLibrarySearchDefaultDirs = 0x00001000;

    /// <summary>
    /// GUI-path bootstrap. If the running exe carries a
    /// <see cref="RuntimeResourceName"/> resource (a stamped <c>Setup.exe</c>),
    /// extract the embedded native DLLs to a verified cache directory and add that
    /// directory to the native DLL search path. Idempotent: a second call — or a
    /// re-run of the same stamped exe — reuses the already-extracted directory once
    /// its contents check out, and never duplicates work. Returns the extraction
    /// directory, or <c>null</c>
    /// when no resource is present (an un-stamped dev run whose native DLLs already
    /// sit beside the exe, so nothing to do).
    /// </summary>
    /// <param name="report">
    /// Optional <c>(message, isError)</c> sink for the one thing an operator needs to
    /// see: that a cache directory was discarded because its contents did not match
    /// the embedded archive. That is either an interrupted extraction or R4's attack,
    /// and both are worth a line in the wizard log.
    /// </param>
    [SupportedOSPlatform("windows")]
    public static string? EnsureNativeDependenciesLoadable(Action<string, bool>? report = null)
    {
        var archive = WrapperBlob.LoadRuntimeBytes();
        if (archive is null || archive.Length == 0)
        {
            return null;
        }

        // Elevated is the case that matters: Installer.Host calls this AFTER the
        // elevation branch, so on the default double-click GUI install this runs inside
        // the elevated process and whatever lands on the DLL search path is loaded with
        // administrator rights.
        var elevated = Elevation.IsProcessElevated();
        var targetDir = PrepareCacheDirectory(archive, ResolveCacheRoot(elevated), elevated, report);
        AddNativeSearchDirectory(targetDir);
        return targetDir;
    }

    /// <summary>
    /// The root the content-keyed cache directories live under:
    /// <c>%ProgramData%\sigil-runtime</c> for an elevated run (established
    /// administrator-only by <see cref="PrepareCacheDirectory"/>), the historical
    /// <c>%LocalAppData%\Sigil\runtime</c> otherwise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>%LocalAppData%</c> is per-user, so unelevated there is no privilege boundary
    /// to cross and no better location exists — the attacker and the victim are the same
    /// account. Elevated there very much is one, and <c>%LocalAppData%</c> is on the
    /// wrong side of it: that is register row R4.
    /// </para>
    /// <para>
    /// <b>Deliberately a direct child of <c>%ProgramData%</c>, and deliberately not
    /// under <c>%ProgramData%\Sigil</c>.</b> That path is the install-state store's root
    /// and must not be repaired from here; depending on it instead meant that any
    /// non-administrator who pre-created it — register row R1's attack, which needs no
    /// privilege — blocked every elevated GUI install, because this bootstrap would then
    /// refuse. <c>sigil-runtime</c> is this component's own directory, so
    /// <see cref="StateDirectorySecurity.CreateHardened"/> may re-permission and take
    /// ownership of a squatted one, which turns that denial of service back into a
    /// no-op. <c>%ProgramData%</c> itself grants <c>BUILTIN\Users</c> <c>(CI)(WD,AD)</c>
    /// but <b>not</b> <c>DC</c>, so a non-administrator can create a sibling there and
    /// can never delete or replace this one once it is ours.
    /// </para>
    /// </remarks>
    public static string ResolveCacheRoot(bool elevated) =>
        elevated
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "sigil-runtime")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Sigil",
                "runtime");

    /// <summary>
    /// Resolve the stable cache directory for an archive: keyed by the archive's
    /// SHA-256 so identical native-dep sets share one extraction and a version bump
    /// lands in a fresh directory.
    /// </summary>
    /// <remarks>
    /// The SHA-256 name is a cache key, <b>not</b> a defence: the archive is readable
    /// straight out of the setup exe, so the directory name is derivable by anyone
    /// holding the installer. Trust comes from <see cref="PrepareCacheDirectory"/>.
    /// </remarks>
    internal static string ResolveCacheDirectory(byte[] archiveBytes) =>
        Path.Combine(ResolveCacheRoot(Elevation.IsProcessElevated()), ArchiveKey(archiveBytes));

    /// <summary>
    /// Produce a cache directory under <paramref name="cacheRoot"/> whose contents are
    /// known to be exactly <paramref name="archiveBytes"/>, and return it. This is the
    /// fix for register row R4.
    /// </summary>
    /// <param name="requireAdminOnlyRoot">
    /// True for an elevated run. The root and the cache directory must then pass
    /// <see cref="StateDirectorySecurity.IsAdminOnlyWritable"/>, and the call
    /// <b>throws</b> rather than proceeding if they cannot be made to — see the
    /// remarks.
    /// </param>
    /// <exception cref="NativeRuntimeTrustException">
    /// The directory could not be established, cleaned, or verified. Refusing is the
    /// point: the alternative is registering an attacker-controlled directory on the
    /// process DLL search path.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>What was wrong.</b> The old code took <c>File.Exists(marker)</c> as proof the
    /// extraction had happened and skipped it wholesale, then handed the directory to
    /// <see cref="AddNativeSearchDirectory"/>. Any process running as the user could
    /// pre-create the (derivable) content-keyed directory, drop a hostile
    /// <c>libSkiaSharp.dll</c> in it, <c>touch</c> the marker, and have the elevated
    /// wizard load it. Even without the marker the incremental path compared only file
    /// <em>length</em>, which an attacker controls exactly.
    /// </para>
    /// <para>
    /// <b>Three things replace it.</b> (1) Every file is hashed against the embedded
    /// archive before the directory is used. (2) A file in the directory that the
    /// archive does not contain is a mismatch too — a planted <c>version.dll</c> beside
    /// genuine Skia binaries is just as loadable. (3) Elevated, the directory is sited
    /// under an administrator-only root, because content verification alone still leaves
    /// the window between the last hash and <c>LoadLibrary</c>, and only an ACL closes
    /// that.
    /// </para>
    /// <para>
    /// <b>Why an elevated run refuses rather than degrades.</b> Continuing means
    /// executing attacker-replaceable native code as administrator; there is no
    /// weakened-but-useful version of that.
    /// </para>
    /// <para>
    /// <b>And why that refusal is not a squatting lever.</b> The root and the cache
    /// directory are both this component's own (<c>%ProgramData%\sigil-runtime\…</c>,
    /// never <c>%ProgramData%\Sigil</c>), so a pre-created one is <em>repaired</em> —
    /// <see cref="StateDirectorySecurity.CreateHardened"/> re-applies the protected DACL
    /// and hands ownership to <c>BUILTIN\Administrators</c>, which an elevated caller
    /// can do — and the run continues. Nothing an unprivileged user can create makes
    /// this refuse; only a genuinely broken or hostile environment does. The parent
    /// <c>%ProgramData%</c> is deliberately <b>not</b> required to be administrator-only:
    /// it grants <c>BUILTIN\Users</c> <c>(CI)(WD,AD)</c> but not <c>DC</c>, so a
    /// non-administrator can add siblings and can never delete or replace ours.
    /// </para>
    /// </remarks>
    public static string PrepareCacheDirectory(
        byte[] archiveBytes, string cacheRoot, bool requireAdminOnlyRoot, Action<string, bool>? report)
    {
        ArgumentNullException.ThrowIfNull(archiveBytes);
        ArgumentException.ThrowIfNullOrEmpty(cacheRoot);

        var root = Path.GetFullPath(cacheRoot);
        var targetDir = Path.Combine(root, ArchiveKey(archiveBytes));

        if (requireAdminOnlyRoot)
        {
            if (OperatingSystem.IsWindows())
            {
                EstablishAdminOnlyRoot(root);
            }
            else
            {
                throw new NativeRuntimeTrustException(
                    "an administrator-only native runtime cache is a Windows-only concept, " +
                    $"so '{root}' cannot be established");
            }
        }

        // The marker is a fast path and nothing more: it is consulted only alongside the
        // ACL check and a full content comparison, never instead of them.
        if (File.Exists(Path.Combine(targetDir, CompletionMarkerName))
            && IsSitedSafely(targetDir, requireAdminOnlyRoot)
            && MatchesArchive(archiveBytes, targetDir))
        {
            return targetDir;
        }

        Discard(targetDir, report);

        if (requireAdminOnlyRoot && OperatingSystem.IsWindows())
        {
            StateDirectorySecurity.CreateHardened(targetDir);
        }

        ExtractArchive(archiveBytes, targetDir);

        // Verified AFTER writing, before the directory can reach the DLL search path:
        // this is what makes the marker written below mean something.
        if (!MatchesArchive(archiveBytes, targetDir))
        {
            throw new NativeRuntimeTrustException(
                $"the native runtime cache '{targetDir}' does not match the embedded archive after a " +
                "fresh extraction; refusing to add it to the DLL search path");
        }

        if (!IsSitedSafely(targetDir, requireAdminOnlyRoot))
        {
            throw new NativeRuntimeTrustException(
                $"'{targetDir}' is not administrator-only writable, so a non-administrator could replace " +
                "a native DLL in it between this check and the load; refusing to add it to the DLL search path");
        }

        File.WriteAllBytes(Path.Combine(targetDir, CompletionMarkerName), Array.Empty<byte>());
        return targetDir;
    }

    /// <summary>The archive's SHA-256, lower-case hex — the cache directory's name.</summary>
    private static string ArchiveKey(byte[] archiveBytes) =>
        Convert.ToHexString(SHA256.HashData(archiveBytes)).ToLowerInvariant();

    /// <summary>
    /// True when <paramref name="directory"/> is sited where only a privileged principal
    /// can alter it — trivially true when this run is not elevated, since there is then
    /// no boundary for anyone to cross.
    /// </summary>
    private static bool IsSitedSafely(string directory, bool requireAdminOnlyRoot) =>
        !requireAdminOnlyRoot
        || (OperatingSystem.IsWindows() && StateDirectorySecurity.IsAdminOnlyWritable(directory));

    /// <summary>
    /// Establish <paramref name="cacheRoot"/> (<c>%ProgramData%\sigil-runtime</c>) as an
    /// administrator-only directory, or throw.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>cacheRoot</c> is <b>this component's own</b> directory, so a pre-existing
    /// permissive one is <em>repaired</em> rather than refused:
    /// <see cref="StateDirectorySecurity.CreateHardened"/> re-applies the protected DACL
    /// and hands ownership to <c>BUILTIN\Administrators</c>. That is what keeps a
    /// non-administrator from turning this refusal into a denial of service by simply
    /// creating the directory first. It is also why the root deliberately does not live
    /// under <c>%ProgramData%\Sigil</c>: that one belongs to the install-state store and
    /// repairing it as a side effect of extracting native DLLs would be far too broad.
    /// </para>
    /// <para>
    /// The <b>parent</b> is not checked, on purpose. <c>%ProgramData%</c> grants
    /// <c>BUILTIN\Users</c> <c>(CI)(WD,AD,WEA,WA)</c> — create-child — but not
    /// <c>DC</c> (delete-child), so a non-administrator can add siblings and can neither
    /// delete nor replace a directory it does not own. Requiring the parent to be
    /// administrator-only would fail for <c>%ProgramData%</c> itself and refuse every
    /// legitimate elevated install.
    /// </para>
    /// </remarks>
    [SupportedOSPlatform("windows")]
    private static void EstablishAdminOnlyRoot(string cacheRoot)
    {
        StateDirectorySecurity.CreateHardened(cacheRoot);
        if (!StateDirectorySecurity.IsAdminOnlyWritable(cacheRoot))
        {
            throw new NativeRuntimeTrustException(
                $"'{cacheRoot}' could not be made administrator-only writable; refusing to extract the " +
                "native runtime there");
        }
    }

    /// <summary>
    /// Remove a cache directory whose contents were not the archive's. Failing to remove
    /// it is a refusal, never a fallback to using it.
    /// </summary>
    private static void Discard(string targetDir, Action<string, bool>? report)
    {
        if (!Directory.Exists(targetDir))
        {
            return;
        }

        report?.Invoke(
            $"native runtime: the cache directory '{targetDir}' does not match the embedded archive — " +
            "discarding it and extracting again",
            true);

        try
        {
            Directory.Delete(targetDir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new NativeRuntimeTrustException(
                $"the native runtime cache '{targetDir}' does not match the embedded archive and could " +
                $"not be discarded ({ex.GetType().Name}: {ex.Message}); refusing to use it",
                ex);
        }
    }

    /// <summary>
    /// True when <paramref name="targetDir"/> holds exactly the archive's files, byte for
    /// byte, and nothing else. Both halves matter: a wrong <c>libSkiaSharp.dll</c> and an
    /// extra <c>version.dll</c> the archive never contained are equally loadable once the
    /// directory is on the DLL search path. Fails closed — an unreadable entry answers
    /// <c>false</c>, which discards and re-extracts.
    /// </summary>
    private static bool MatchesArchive(byte[] archiveBytes, string targetDir)
    {
        if (!Directory.Exists(targetDir))
        {
            return false;
        }

        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(targetDir, CompletionMarkerName),
        };

        try
        {
            using var ms = new MemoryStream(archiveBytes, writable: false);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name) ||
                    entry.FullName.EndsWith('/') ||
                    entry.FullName.EndsWith('\\'))
                {
                    continue;
                }

                var dest = ResolveEntryPath(targetDir, entry.FullName);
                expected.Add(dest);
                for (var dir = Path.GetDirectoryName(dest);
                     !string.IsNullOrEmpty(dir) && dir.Length > targetDir.Length;
                     dir = Path.GetDirectoryName(dir))
                {
                    expected.Add(dir);
                }

                if (!File.Exists(dest))
                {
                    return false;
                }

                byte[] fromArchive;
                using (var entryStream = entry.Open())
                {
                    fromArchive = SHA256.HashData(entryStream);
                }

                byte[] onDisk;
                using (var fs = new FileStream(dest, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    onDisk = SHA256.HashData(fs);
                }

                if (!CryptographicOperations.FixedTimeEquals(fromArchive, onDisk))
                {
                    return false;
                }
            }

            // Anything the archive did not put here is a planted file: a DLL the loader
            // would happily resolve by name once this directory is searched.
            foreach (var found in Directory.EnumerateFileSystemEntries(
                targetDir, "*", SearchOption.AllDirectories))
            {
                if (!expected.Contains(found))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Fail closed: an unreadable or malformed cache is discarded, not adopted.
            return false;
        }
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

/// <summary>
/// The native-dependency cache directory could not be established, cleaned or verified,
/// so it was <b>not</b> added to the process DLL search path (register row R4). Distinct
/// from a plain <see cref="IOException"/> because it is a deliberate refusal rather than
/// an incidental I/O failure: the host reports it and exits instead of starting a wizard
/// whose native code might not be ours.
/// </summary>
public sealed class NativeRuntimeTrustException : Exception
{
    public NativeRuntimeTrustException(string message)
        : base(message)
    {
    }

    public NativeRuntimeTrustException()
    {
    }

    public NativeRuntimeTrustException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
