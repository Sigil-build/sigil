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

    /// <summary>
    /// Directory-name prefix of the per-run fallback root created when the shared cache
    /// root cannot be established (see <see cref="EstablishAdminOnlyRoot"/>). The full
    /// name is this plus a 32-hex-digit GUID. Both the creator and
    /// <see cref="ReclaimAbandonedFallbacks"/> derive names from this one constant, so
    /// the sweep can never match a directory the fallback path did not create — in
    /// particular not the shared root <c>sigil-runtime</c> itself, and not
    /// <c>%ProgramData%\Sigil</c>.
    /// </summary>
    private const string FallbackPrefix = "sigil-runtime-";

    /// <summary>
    /// File this process keeps an OPEN, delete-denying handle on for its whole lifetime
    /// inside every per-run fallback root it creates. It is the interlock that makes
    /// reclaiming safe — see <see cref="ReclaimAbandonedFallbacks"/>.
    /// </summary>
    private const string LeaseFileName = ".sigil-runtime-lease";

    /// <summary>
    /// How old a fallback directory carrying <b>no</b> lease file must be before it may
    /// be reclaimed. Covers exactly two cases: a directory created by a build from
    /// before the lease existed, and the sub-millisecond window in the current build
    /// between creating a fallback root and opening its lease. An hour is far longer
    /// than either and far shorter than "never".
    /// </summary>
    private static readonly TimeSpan UnleasedReclaimGrace = TimeSpan.FromHours(1);

    private static readonly object LeaseGate = new();

    /// <summary>
    /// Open lease handles, one per fallback root this process created, alongside the
    /// directories they cover. Never disposed: the point is that they stay open until
    /// the process exits, because the native DLLs extracted beside them stay mapped
    /// into this process for exactly that long.
    /// </summary>
    private static readonly List<FileStream> Leases = new();

    private static readonly List<string> LeasedDirectories = new();

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
    /// can do — and the run continues. A squat that cannot even be repaired (a
    /// <em>file</em> at that path, an owner-pinned deny ACE) costs the run its shared
    /// cache and nothing else: <c>EstablishAdminOnlyRoot</c> falls back to a per-run GUID
    /// directory beside it. Nothing an unprivileged user can create makes this refuse.
    /// The parent <c>%ProgramData%</c> is deliberately <b>not</b> required to be
    /// administrator-only: it grants <c>BUILTIN\Users</c> <c>(CI)(WD,AD)</c> but not
    /// <c>DC</c>, so a non-administrator can add siblings and can never delete or replace
    /// ours.
    /// </para>
    /// </remarks>
    public static string PrepareCacheDirectory(
        byte[] archiveBytes, string cacheRoot, bool requireAdminOnlyRoot, Action<string, bool>? report)
    {
        ArgumentNullException.ThrowIfNull(archiveBytes);
        ArgumentException.ThrowIfNullOrEmpty(cacheRoot);

        var root = Path.GetFullPath(cacheRoot);

        // R50: before this run possibly adds a fallback of its own, remove the ones
        // earlier runs left behind. Sited here rather than at process exit because the
        // directory a run leaks is the one whose DLLs it still has mapped — it cannot
        // clean up after itself, only after its predecessors. The sweep touches nothing
        // it cannot prove is abandoned; see ReclaimAbandonedFallbacks.
        _ = ReclaimAbandonedFallbacks(root, requireAdminOnlyRoot, report);

        if (requireAdminOnlyRoot)
        {
            if (OperatingSystem.IsWindows())
            {
                // May hand back a DIFFERENT root than the one asked for — see the method.
                root = EstablishAdminOnlyRoot(root, report);
            }
            else
            {
                throw new NativeRuntimeTrustException(
                    "an administrator-only native runtime cache is a Windows-only concept, " +
                    $"so '{root}' cannot be established");
            }
        }

        var targetDir = Path.Combine(root, ArchiveKey(archiveBytes));

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
            // The ownership-repair diagnostic goes to the same sink as everything else
            // here: taking ownership of an existing machine-scope directory must not be
            // a silent event.
            StateDirectorySecurity.CreateHardened(targetDir, ReportSink.For(report));
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

    /// <summary>
    /// Delete per-run fallback roots (<c>&lt;parent&gt;\sigil-runtime-&lt;guid&gt;</c>)
    /// left behind by runs that have ended, and report how many were reclaimed. This is
    /// the fix for register row R50.
    /// </summary>
    /// <param name="cacheRoot">
    /// The shared cache root; its <em>parent</em> is the directory swept, because that
    /// is where <see cref="EstablishAdminOnlyRoot"/> sites its fallbacks.
    /// </param>
    /// <param name="requireAdminOnlyRoot">
    /// True for an elevated run, in which case a candidate must also prove
    /// administrator-only writability <em>through the open handle</em> before it is
    /// touched. Unelevated there is no privilege boundary to cross, exactly as in
    /// <see cref="IsSitedSafely"/>.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>The leak.</b> When the shared root cannot be established or repaired — a
    /// <em>file</em> at that path, an owner-pinned deny ACE — the run extracts ~18 MB
    /// into a fresh per-run GUID directory instead. That directory was never cleaned
    /// up, and the squat that caused it is permanent, so one <c>New-Item</c> by any
    /// unprivileged user armed an unbounded per-install disk leak. The refusal itself
    /// is the design and is not changed here; only the litter is.
    /// </para>
    /// <para>
    /// <b>Constraint 1 — guards read from an open handle, never through the path.</b>
    /// Every decision about a candidate is made from a handle opened with
    /// <c>FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT</c>, so a junction
    /// or symlink planted at the candidate's name is opened <em>as the link</em> and
    /// refused on its <c>FILE_ATTRIBUTE_REPARSE_POINT</c> bit rather than silently
    /// followed to whatever it targets. Attributes, creation time and the security
    /// descriptor all come from that one handle
    /// (<see cref="StateDirectorySecurity.IsAdminOnlyWritableHandle"/>), so they
    /// describe one pinned kernel object rather than three successive path lookups.
    /// </para>
    /// <para>
    /// <b>Why the final delete may then be by path.</b> A directory that passed the
    /// administrator-only check cannot be swapped by a non-administrator between the
    /// check and the delete: <c>%ProgramData%</c> grants <c>BUILTIN\Users</c>
    /// create-child but <b>not</b> <c>DC</c>, so they cannot remove or rename ours, and
    /// the directory's own protected DACL denies them write. A candidate that
    /// <em>fails</em> the check is left strictly alone — never deleted — so nothing an
    /// unprivileged user controls is ever removed by this sweep. The guard is what
    /// licenses the path-based delete; without it the delete would not be safe.
    /// </para>
    /// <para>
    /// <b>Constraint 2 — a reclaim must not race a live install.</b> Two layers, both
    /// enforced by the kernel rather than by timing:
    /// <list type="number">
    ///   <item><description>
    ///   <b>The lease.</b> A process that creates a fallback root opens
    ///   <c>.sigil-runtime-lease</c> inside it with <c>FileShare.Read</c> — no
    ///   <c>Delete</c>, no <c>Write</c> — and holds that handle until it exits. A
    ///   sweeper trying to open the same file with <see cref="FileShare.None"/> gets a
    ///   sharing violation and abandons the whole directory.
    ///   </description></item>
    ///   <item><description>
    ///   <b>The probe, before anything is removed.</b> EVERY file in the candidate is
    ///   opened <see cref="FileShare.None"/> and all the handles are held at once. If a
    ///   single open fails the sweep releases them and deletes <em>nothing</em>. A DLL
    ///   mapped as an image into a live installer cannot be opened that way, so a
    ///   directory still in use by a process from before the lease existed is caught
    ///   too. This is why the probe is a separate all-or-nothing phase rather than
    ///   delete-and-see: <c>Directory.Delete(recursive)</c> removes files one at a time
    ///   and would already have destroyed part of a live install before reaching the
    ///   file that stopped it.
    ///   </description></item>
    /// </list>
    /// A directory with no lease file at all is additionally required to be
    /// <see cref="UnleasedReclaimGrace"/> old, which removes the only remaining window:
    /// a fallback created microseconds ago whose lease is not yet open, and which is
    /// therefore still empty and would otherwise probe clean.
    /// </para>
    /// <para>
    /// <b>Failure is always "leave it alone".</b> Every unreadable, unopenable or
    /// surprising candidate is skipped. The cost of skipping is the leak this method
    /// exists to fix; the cost of guessing wrong in the other direction is deleting a
    /// live installer's native DLLs mid-install.
    /// </para>
    /// </remarks>
    internal static int ReclaimAbandonedFallbacks(
        string cacheRoot, bool requireAdminOnlyRoot, Action<string, bool>? report)
    {
        ArgumentException.ThrowIfNullOrEmpty(cacheRoot);

        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }

        var parent = Path.GetDirectoryName(Path.GetFullPath(cacheRoot));
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
        {
            return 0;
        }

        string[] candidates;
#pragma warning disable CA1031 // An unreadable parent is a no-op sweep, never a failed install.
        try
        {
            candidates = Directory.GetDirectories(parent, FallbackPrefix + "*");
        }
        catch
        {
            return 0;
        }
#pragma warning restore CA1031

        var reclaimed = 0;
        foreach (var candidate in candidates)
        {
            if (!IsFallbackDirectoryName(Path.GetFileName(candidate))
                || IsLeasedByThisProcess(candidate))
            {
                continue;
            }

            if (TryReclaim(candidate, requireAdminOnlyRoot, report))
            {
                reclaimed++;
            }
        }

        return reclaimed;
    }

    /// <summary>
    /// Exactly <c>sigil-runtime-</c> followed by 32 lower-case hex digits — the shape
    /// <see cref="EstablishAdminOnlyRoot"/> produces with <c>Guid.NewGuid():N</c>, and
    /// nothing else. The shared root <c>sigil-runtime</c> fails this (no suffix), as
    /// does anything a human or another component named.
    /// </summary>
    private static bool IsFallbackDirectoryName(string? name)
    {
        if (name is null || name.Length != FallbackPrefix.Length + 32)
        {
            return false;
        }

        if (!name.StartsWith(FallbackPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        for (var i = FallbackPrefix.Length; i < name.Length; i++)
        {
            var c = name[i];
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsLeasedByThisProcess(string directory)
    {
        lock (LeaseGate)
        {
            foreach (var leased in LeasedDirectories)
            {
                if (string.Equals(leased, directory, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    [SupportedOSPlatform("windows")]
    private static bool TryReclaim(
        string candidate, bool requireAdminOnlyRoot, Action<string, bool>? report)
    {
#pragma warning disable CA1031 // Every failure mode here means "leave it alone"; see the remarks on ReclaimAbandonedFallbacks.
        try
        {
            using (var handle = DirectoryHandle.OpenNoFollow(candidate, LeaseFileName))
            {
                if (handle is null)
                {
                    return false; // vanished, or we may not open it — either way, not ours to remove.
                }

                if (!handle.IsPlainDirectory)
                {
                    report?.Invoke(
                        $"native runtime: '{candidate}' has the shape of an abandoned per-run cache but " +
                        "is a reparse point, not a directory; refusing to follow it — nothing was deleted",
                        true);
                    return false;
                }

                if (requireAdminOnlyRoot
                    && !StateDirectorySecurity.IsAdminOnlyWritableHandle(handle.Handle))
                {
                    return false; // not provably ours; a non-administrator may control it.
                }

                if (!handle.HasLeaseFile
                    && DateTime.UtcNow - handle.CreationTimeUtc < UnleasedReclaimGrace)
                {
                    return false; // possibly a fallback being created right now by another run.
                }
            }

            if (!IsWhollyUnused(candidate))
            {
                return false; // a live install is using it. Nothing has been touched.
            }

            Directory.Delete(candidate, recursive: true);
            report?.Invoke(
                $"native runtime: reclaimed the abandoned per-run cache directory '{candidate}' " +
                "left by an earlier run (register row R50)",
                false);
            return true;
        }
        catch
        {
            return false;
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// True when every file under <paramref name="directory"/> can be opened with
    /// <see cref="FileShare.None"/> <b>at the same time</b> — i.e. no other process
    /// holds any of them open, and none is mapped as a loaded image. All-or-nothing on
    /// purpose: see <see cref="ReclaimAbandonedFallbacks"/>'s remarks on why probing
    /// has to complete before a single byte is deleted.
    /// </summary>
    private static bool IsWhollyUnused(string directory)
    {
        var held = new List<FileStream>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                held.Add(new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
            }

            return true;
        }
#pragma warning disable CA1031 // Any failure to prove the directory idle answers "in use".
        catch
        {
            return false;
        }
#pragma warning restore CA1031
        finally
        {
            foreach (var stream in held)
            {
                stream.Dispose();
            }
        }
    }

    /// <summary>
    /// Take this process's lease on a per-run fallback root: create
    /// <see cref="LeaseFileName"/> inside it and keep the handle open, sharing read
    /// only, for the rest of the process's life. Returns false when the lease cannot be
    /// taken, which makes the caller abandon that fallback rather than extract into a
    /// directory another run could reclaim underneath it.
    /// </summary>
    private static bool TryTakeLease(string directory, Action<string, bool>? report)
    {
#pragma warning disable CA1031 // Reported and turned into "do not use this directory".
        try
        {
            // FileShare.Read: readers welcome, deleters not. A sweeper's FileShare.None
            // open of this same file is what fails, and that is the interlock.
            var lease = new FileStream(
                Path.Combine(directory, LeaseFileName),
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.Read);

            lock (LeaseGate)
            {
                Leases.Add(lease);
                LeasedDirectories.Add(directory);
            }

            return true;
        }
        catch (Exception ex)
        {
            report?.Invoke(
                $"native runtime: could not lease the private cache directory '{directory}' " +
                $"({ex.GetType().Name}: {ex.Message}); not using it",
                true);
            return false;
        }
#pragma warning restore CA1031
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
    /// Establish an administrator-only root to extract into, and return it: the shared
    /// content-keyed <paramref name="cacheRoot"/> (<c>%ProgramData%\sigil-runtime</c>) when
    /// it can be had, otherwise a fresh per-run GUID directory beside it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>cacheRoot</c> is <b>this component's own</b> directory, so a pre-existing
    /// permissive one is <em>repaired</em> rather than refused:
    /// <see cref="StateDirectorySecurity.CreateHardened"/> re-applies the protected DACL
    /// and hands ownership to <c>BUILTIN\Administrators</c>. That is why the root
    /// deliberately does not live under <c>%ProgramData%\Sigil</c>, which belongs to the
    /// install-state store and must not be repaired from here.
    /// </para>
    /// <para>
    /// <b>Why a shared cache failure must not be fatal.</b> The root's name has to be
    /// stable for it to work as a cache, so it stays pre-creatable — and a squatter does
    /// not have to make it merely permissive. Creating a <em>file</em> at that path, or a
    /// directory with an owner-pinned deny ACE, makes <c>CreateHardened</c> throw. If that
    /// aborted the run, one <c>New-Item</c> from any non-administrator would stop every
    /// elevated GUI install. So the fallback is a per-run GUID directory: unguessable,
    /// therefore un-squattable, and undeletable by a non-administrator once created —
    /// <c>%ProgramData%</c> grants <c>BUILTIN\Users</c> <c>(CI)(WD,AD,WEA,WA)</c>,
    /// create-child, but not <c>DC</c>. The attacker can cost the install its cache; it
    /// cannot cost it the install. The security floor is untouched: the fallback is
    /// created hardened and confirmed by the same predicate, and if <em>it</em> cannot be
    /// established the run still refuses rather than extracting somewhere writable.
    /// </para>
    /// <para>
    /// The <b>parent</b> is not checked, on purpose: <c>%ProgramData%</c> is never
    /// administrator-only, so requiring it would refuse every legitimate elevated install.
    /// </para>
    /// </remarks>
    [SupportedOSPlatform("windows")]
    private static string EstablishAdminOnlyRoot(string cacheRoot, Action<string, bool>? report)
    {
        if (TryEstablish(cacheRoot, report, out var failure))
        {
            return cacheRoot;
        }

        // Losing the shared cache means re-extracting ~18 MB on this run and leaving the
        // directory behind, since its DLLs stay loaded for the process's lifetime. Both
        // are costs worth paying to keep the install running.
        var parent = Path.GetDirectoryName(cacheRoot);
        var perRun = Path.Combine(
            string.IsNullOrEmpty(parent) ? cacheRoot : parent,
            FallbackPrefix + Guid.NewGuid().ToString("N"));

        report?.Invoke(
            $"native runtime: '{cacheRoot}' could not be established as an administrator-only cache " +
            $"({failure}); extracting to the private directory '{perRun}' for this run instead",
            true);

        // R50: lease it in the same breath as establishing it. The lease is what lets a
        // LATER run tell "abandoned, reclaim it" from "in use, leave it alone", and a
        // fallback we cannot lease is one we must not extract into — a concurrent run's
        // sweep would be entitled to remove it out from under us once the grace period
        // elapsed. Losing it here falls through to the same refusal as failing to
        // establish it.
        if (TryEstablish(perRun, report, out var fallbackFailure) && TryTakeLease(perRun, report))
        {
            return perRun;
        }

        // Nothing was extracted into it, and its name is never reused, so leaving it would
        // be pure litter.
#pragma warning disable CA1031 // Cleanup of our own failed creation; the refusal below is what matters.
        try
        {
            if (Directory.Exists(perRun))
            {
                Directory.Delete(perRun, recursive: true);
            }
        }
        catch
        {
            // Untidy, never unsafe.
        }
#pragma warning restore CA1031

        throw new NativeRuntimeTrustException(
            $"neither '{cacheRoot}' ({failure}) nor '{perRun}' ({fallbackFailure}) could be made " +
            "administrator-only writable; refusing to extract the native runtime into a directory a " +
            "non-administrator could rewrite");
    }

    /// <summary>
    /// Create <paramref name="directory"/> hardened and confirm it is administrator-only.
    /// Any I/O or access failure is reported through <paramref name="failure"/> rather
    /// than thrown, so the caller can fall back instead of aborting the install.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static bool TryEstablish(string directory, Action<string, bool>? report, out string failure)
    {
#pragma warning disable CA1031 // The caller's whole job is to survive this; the cause travels out in `failure`.
        try
        {
            StateDirectorySecurity.CreateHardened(directory, ReportSink.For(report));
        }
        catch (Exception ex)
        {
            failure = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
#pragma warning restore CA1031

        if (!StateDirectorySecurity.IsAdminOnlyWritable(directory))
        {
            failure = "it is not administrator-only writable";
            return false;
        }

        failure = string.Empty;
        return true;
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
