namespace SigilBuild.Wrapper.Engine;

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

/// <summary>
/// A single thing blocking the install / uninstall: either a declared
/// <c>installer.app_mutex</c> that is currently held, or a process the Restart
/// Manager reports as holding a file open under the install directory.
/// </summary>
/// <param name="Name">Friendly name — the mutex name, or the RM-reported application name.</param>
/// <param name="ProcessId">The owning process id; <c>0</c> for a mutex blocker (the owner is not knowable from the handle).</param>
/// <param name="FromMutex">True when detected via <c>installer.app_mutex</c> rather than the Restart Manager.</param>
public readonly record struct AppBlocker(string Name, uint ProcessId, bool FromMutex)
{
    /// <summary>A one-line description for the log / the wizard list.</summary>
    public string Describe() => FromMutex
        ? $"{Name} (application mutex held)"
        : ProcessId == 0 ? Name : $"{Name} (pid {ProcessId})";
}

/// <summary>
/// Files-in-use detection — the Inno <c>AppMutex</c> +
/// <c>CloseApplications</c> equivalent. Two independent probes:
/// <list type="number">
///   <item><description><b>Declared mutexes</b> — <c>OpenMutexW</c> on each
///   <c>installer.app_mutex</c> name. Opening succeeds only while the app holds it,
///   so this catches a running app even when it has no files open yet.</description></item>
///   <item><description><b>Restart Manager</b> — register the existing files under the
///   resolved install directory and ask <c>RmGetList</c> which processes hold them.
///   This is what finds a running app on an upgrade / uninstall.</description></item>
/// </list>
/// <see cref="CloseBlockers"/> asks the Restart Manager to shut those processes down
/// gracefully. <b>Restart is never attempted</b> (<c>RmRestart</c> is not called) and
/// there is no force-kill fallback — both are explicit non-goals.
/// </summary>
/// <remarks>
/// <para>All interop is source-generated <c>[LibraryImport]</c> (Native-AOT safe). Every
/// probe is best-effort: on any RM failure the sweep reports no blockers rather than
/// failing the install — a false "clear" degrades to the step engine surfacing the
/// locked-file error and rolling back, whereas a false "blocked" would wedge a
/// perfectly good install.</para>
/// <para>The one thing the RM sweep never reports is the installer itself — see
/// <see cref="IsRunningInstallerProcess"/>. The uninstaller runs from inside the
/// directory this sweep registers, so without that exclusion the gate refuses its own
/// ARP <c>UninstallString</c>. (R58)</para>
/// </remarks>
public static partial class FilesInUse
{
    /// <summary>
    /// Registering every file of a large install would exceed what the Restart Manager
    /// usefully handles; the sweep registers at most this many files (deterministically
    /// ordered). Any process holding a file beyond the cap is still caught later by the
    /// step engine's own locked-file failure.
    /// </summary>
    private const int MaxRegisteredFiles = 1000;

    /// <summary>
    /// Probe the declared mutexes and sweep <paramref name="installDir"/> with the
    /// Restart Manager. Returns every distinct blocker found (empty = clear) — never
    /// this installer's own process (R58, see <see cref="IsRunningInstallerProcess"/>).
    /// Never throws; always empty off Windows.
    /// </summary>
    public static IReadOnlyList<AppBlocker> Scan(IReadOnlyList<string>? appMutexes, string? installDir)
        => Scan(appMutexes, installDir, SelfImagePath());

    /// <summary>
    /// <see cref="Scan(IReadOnlyList{string}, string)"/> with the running installer's own
    /// image path supplied rather than read from <see cref="Environment.ProcessPath"/>.
    /// </summary>
    /// <param name="appMutexes">The declared <c>installer.app_mutex</c> names.</param>
    /// <param name="installDir">The directory to sweep.</param>
    /// <param name="selfImagePath">
    /// The image path treated as "this installer" for the exclusion (R58); <c>null</c> disables
    /// the same-image half, leaving only the own-pid check.
    /// </param>
    /// <remarks>
    /// The seam exists so a test can supply an image it can actually launch a second
    /// process from: a test host cannot spawn a copy of ITSELF to stand in for the
    /// elevated relaunch pair, so without this parameter the same-image branch could only
    /// be tested through its predicate and never through <c>Scan</c>'s own output, which
    /// leaves the wiring unpinned (passing <c>null</c> here keeps every test green).
    /// Production always calls the two-argument overload above.
    /// </remarks>
    internal static IReadOnlyList<AppBlocker> Scan(
        IReadOnlyList<string>? appMutexes, string? installDir, string? selfImagePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<AppBlocker>();
        }

        var found = new List<AppBlocker>();
        found.AddRange(ScanMutexes(appMutexes));
        found.AddRange(ScanRestartManager(installDir, selfImagePath));

        // De-dupe on (name, pid) — a process can be reported by both probes.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<AppBlocker>(found.Count);
        foreach (var b in found)
        {
            if (seen.Add(b.Name + "|" + b.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            {
                result.Add(b);
            }
        }
        return result;
    }

    /// <summary>
    /// Open each declared mutex name; a name that opens is held by a running instance.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static List<AppBlocker> ScanMutexes(IReadOnlyList<string>? names)
    {
        var blockers = new List<AppBlocker>();
        if (names is null || names.Count == 0)
        {
            return blockers;
        }

        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }
            var h = OpenMutexW(SYNCHRONIZE, bInheritHandle: false, name);
            if (h != IntPtr.Zero)
            {
                CloseHandle(h);
                blockers.Add(new AppBlocker(name, 0, FromMutex: true));
            }
        }
        return blockers;
    }

    /// <summary>
    /// Ask the Restart Manager which processes hold files open under
    /// <paramref name="installDir"/>. Empty when the directory does not exist yet (a
    /// fresh install has nothing to block).
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static List<AppBlocker> ScanRestartManager(string? installDir, string? selfImagePath)
    {
        var blockers = new List<AppBlocker>();
        var files = EnumerateExistingFiles(installDir);
        if (files.Length == 0)
        {
            return blockers;
        }

        if (!TryStartSession(out var session))
        {
            return blockers;
        }
        try
        {
            if (RmRegisterResources(session, (uint)files.Length, files, 0, null, 0, null) != 0)
            {
                return blockers;
            }
            blockers.AddRange(GetList(session, selfImagePath));
        }
#pragma warning disable CA1031 // Best-effort probe: a RM failure must never wedge a good install.
        catch (Exception)
        {
            // fall through — report no blockers.
        }
#pragma warning restore CA1031
        finally
        {
            _ = RmEndSession(session);
        }
        return blockers;
    }

    /// <summary>
    /// Ask the Restart Manager to shut down the processes holding files under
    /// <paramref name="installDir"/> — a graceful close (apps get their normal
    /// shutdown path), never a force-kill, and <b>never</b> a restart afterwards.
    /// Returns true when the shutdown call succeeded. Declared-mutex blockers cannot
    /// be closed this way (no process handle) — the caller re-scans to confirm.
    /// </summary>
    public static bool CloseBlockers(string? installDir)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }
        return CloseViaRestartManager(installDir);
    }

    [SupportedOSPlatform("windows")]
    private static bool CloseViaRestartManager(string? installDir)
    {
        var files = EnumerateExistingFiles(installDir);
        if (files.Length == 0)
        {
            return true; // nothing registered → nothing to close.
        }

        if (!TryStartSession(out var session))
        {
            return false;
        }
        try
        {
            if (RmRegisterResources(session, (uint)files.Length, files, 0, null, 0, null) != 0)
            {
                return false;
            }
            // lActionFlags = 0 → request a graceful shutdown. RmRestart is deliberately
            // never called: relaunching the user's apps is an explicit non-goal.
            return RmShutdown(session, 0, IntPtr.Zero) == 0;
        }
#pragma warning disable CA1031 // Best-effort close; the caller re-scans to decide.
        catch (Exception)
        {
            return false;
        }
#pragma warning restore CA1031
        finally
        {
            _ = RmEndSession(session);
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool TryStartSession(out uint session)
    {
        // strSessionKey is an OUT buffer — RmStartSession writes CCH_RM_SESSION_KEY
        // chars plus a NUL into it. Never pass a managed `string` here: [LibraryImport]'s
        // UTF-16 marshalling pins it, so the API writes through the string's own buffer,
        // and one refactor to a literal or a cached constant silently mutates an INTERNED
        // string. A char[] behind a `ref char` signature says what actually happens, can
        // never be aliased, and stops the signature advertising "in" for a parameter the
        // API treats as "out". (R38)
        var key = new char[CCH_RM_SESSION_KEY + 1];
        var buffer = MemoryMarshal.Cast<char, ushort>(key.AsSpan());
        return RmStartSession(out session, 0, ref buffer[0]) == 0;
    }

    [SupportedOSPlatform("windows")]
    private static List<AppBlocker> GetList(uint session, string? selfImagePath)
    {
        var blockers = new List<AppBlocker>();

        // First call with an empty buffer to learn how many entries are needed.
        uint count = 0;
        var rc = RmGetList(session, out var needed, ref count, null, out _);
        if (rc == ERROR_SUCCESS || needed == 0)
        {
            return blockers; // nothing holding the files.
        }
        if (rc != ERROR_MORE_DATA)
        {
            return blockers;
        }

        count = needed;
        var infos = new RM_PROCESS_INFO[count];
        if (RmGetList(session, out needed, ref count, infos, out _) != ERROR_SUCCESS)
        {
            return blockers;
        }

        for (var i = 0; i < count; i++)
        {
            var pid = infos[i].Process.dwProcessId;

            // The running installer is never a blocker of ITSELF. Install survivability
            // copies the running setup image into the install directory as
            // `uninstall.exe` and ARP's UninstallString points at that copy, so the
            // uninstaller executes from INSIDE the very directory this sweep registers.
            // The Restart Manager duly reports the uninstaller's own image, so without
            // this exclusion the gate refuses its own ARP entry with exit 4 — "blocked
            // by: installer (pid N)", N being its own pid — before touching anything,
            // and /closeapps cannot rescue it because the Restart Manager cannot close
            // its own caller. The uninstaller's image is not the gate's problem: it is
            // deleted by the reboot-scheduled MoveFileEx (RollbackRecord.RemoveUninstaller
            // / SelfDelete), never by closing an application. This is the ONLY exclusion
            // — a third-party process holding anything under the install directory is
            // still a blocker, including an app whose own image lives there, which is
            // the case the gate exists for. (R58)
            if (IsRunningInstallerProcess(pid, selfImagePath))
            {
                continue;
            }

            var name = ReadFixed(infos[i].strAppName);
            if (string.IsNullOrWhiteSpace(name))
            {
                name = $"pid {pid}";
            }
            blockers.Add(new AppBlocker(name, pid, FromMutex: false));
        }
        return blockers;
    }

    /// <summary>
    /// True when <paramref name="processId"/> is this very process, or another
    /// process running this installer's own executable image. Both are the installer
    /// itself and neither can be a blocker of its own run; everything else is.
    /// </summary>
    /// <remarks>
    /// <para>The second case is self-elevation, not paranoia. A machine-scope ARP
    /// uninstall runs <c>&lt;install_dir&gt;\uninstall.exe /S /Uninstall /allusers</c>
    /// un-elevated, and <see cref="Elevation.RelaunchElevatedAndWait"/> relaunches THE
    /// SAME IMAGE elevated and then sits in <c>WaitForSingleObject</c> until the child
    /// exits. So while the elevated child runs this gate, its un-elevated parent is
    /// still alive with <c>&lt;install_dir&gt;\uninstall.exe</c> loaded as its image —
    /// and the Restart Manager reports a process for a loaded image alone, with no data
    /// file open (asserted in <c>FilesInUseTests</c>). Excluding only the child's own pid
    /// would therefore leave every <c>/allusers</c> ARP uninstall blocked on its own
    /// parent.</para>
    /// <para>The match is on the identity of the image FILE, deliberately not on "is an
    /// ancestor of mine": <c>uninstall.exe</c> is commonly launched from Explorer, and
    /// Explorer very plausibly holds a handle under the install directory, so excluding
    /// the parent process as such would silently disarm the gate for a real blocker. Only
    /// a process running the identical image file is skipped, and the single-instance
    /// guard (<see cref="SetupInstanceLock"/>) is what refuses genuinely concurrent
    /// installer instances — that is not this gate's job.</para>
    /// <para>Fails CLOSED: when either side's identity cannot be resolved (the process
    /// exited, is not openable, or the image cannot be opened) the blocker is kept.</para>
    /// </remarks>
    [SupportedOSPlatform("windows")]
    internal static bool IsRunningInstallerProcess(uint processId, string? selfImagePath)
    {
        if (processId == (uint)Environment.ProcessId)
        {
            return true;
        }
        if (string.IsNullOrEmpty(selfImagePath))
        {
            return false;
        }

        var image = TryGetProcessImagePath(processId);
        return image is not null && SameFile(selfImagePath, image);
    }

    /// <summary>
    /// Whether two paths name the same file on disk, by FILE IDENTITY rather than
    /// by comparing the strings. <c>false</c> whenever either side cannot be resolved,
    /// which keeps the blocker.
    /// </summary>
    /// <remarks>
    /// <para>String comparison is wrong here, and wrong in a way that reinstates the very
    /// defect this exclusion exists to fix. The two sides are produced by different APIs
    /// with different conventions: <see cref="Environment.ProcessPath"/> is
    /// <c>GetModuleFileNameW(NULL)</c>, which preserves the form the process was LAUNCHED
    /// with — 8.3 short components, a <c>subst</c>ed or mapped drive letter, a junction —
    /// whereas <c>QueryFullProcessImageNameW(…, 0)</c> returns the canonical long Win32
    /// path. An ARP row written from a <c>/D=C:\PROGRA~1\Acme</c> install therefore
    /// launches the uninstaller by its short path, the two strings differ, the
    /// relaunch parent is not recognised, and a <c>/allusers</c> ARP uninstall exits 4 —
    /// the exact defect this exclusion removes. Identity has no such convention: the
    /// volume serial plus the file id is the same however the file was named.</para>
    /// <para>Both sides go through this same function, so both yield the same KIND of key
    /// and remain comparable: the 128-bit file id where the filesystem supports it (NTFS,
    /// ReFS), else the fully normalized final path. The fallback matters — a bare
    /// fail-closed on an exotic volume would mean a refused uninstall, so it is worth the
    /// second mechanism rather than fewer moving parts.</para>
    /// </remarks>
    [SupportedOSPlatform("windows")]
    private static bool SameFile(string left, string right)
    {
        var a = TryGetFileIdentity(left);
        if (a is null)
        {
            return false;
        }
        var b = TryGetFileIdentity(right);
        return b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A key identifying the file at <paramref name="path"/> independently of how it is
    /// named, or <c>null</c> when it cannot be opened or identified.
    /// </summary>
    /// <remarks>
    /// Opened with <c>FILE_READ_ATTRIBUTES</c> only, and sharing read/write/delete, so
    /// this can identify a running executable image (held with read+delete sharing) and
    /// never itself becomes the reason someone else's open fails.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    private static unsafe string? TryGetFileIdentity(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var handle = CreateFileW(
            path,
            FILE_READ_ATTRIBUTES,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            IntPtr.Zero,
            OPEN_EXISTING,
            dwFlagsAndAttributes: 0,
            IntPtr.Zero);
        if (handle == INVALID_HANDLE_VALUE)
        {
            return null;
        }
        try
        {
            // Preferred: volume serial + the 128-bit file id.
            FILE_ID_INFO id;
            if (GetFileInformationByHandleEx(
                    handle, FileIdInfo, &id, (uint)sizeof(FILE_ID_INFO)))
            {
                var bytes = new ReadOnlySpan<byte>(id.FileId, FileIdLength);
                return "id:"
                    + id.VolumeSerialNumber.ToString("x16", System.Globalization.CultureInfo.InvariantCulture)
                    + ":" + Convert.ToHexString(bytes);
            }

            // Fallback for a filesystem without file ids: the normalized final path,
            // which resolves short names, junctions and substituted drives alike.
            var buffer = new char[MaxExtendedPathChars];
            var view = MemoryMarshal.Cast<char, ushort>(buffer.AsSpan());
            var written = GetFinalPathNameByHandleW(
                handle, ref view[0], (uint)buffer.Length, VOLUME_NAME_DOS);
            if (written == 0 || written >= buffer.Length)
            {
                return null;
            }
            return "path:" + new string(buffer, 0, (int)written);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>
    /// This process's own executable image path, or <c>null</c> when it is unavailable
    /// (in which case only the pid check applies). Internal so a test can pin the value
    /// the production <see cref="Scan(IReadOnlyList{string}, string)"/> hands the
    /// exclusion.
    /// </summary>
    internal static string? SelfImagePath()
    {
        var path = Environment.ProcessPath;
        return string.IsNullOrEmpty(path) ? null : path;
    }

    /// <summary>
    /// The full image path of another process, or <c>null</c> when it cannot be
    /// determined. Used only to recognise this installer's own image (see
    /// <see cref="IsRunningInstallerProcess"/>).
    /// </summary>
    /// <remarks>
    /// <c>PROCESS_QUERY_LIMITED_INFORMATION</c> is the least privilege that answers this
    /// and is available across integrity levels, which matters because the elevated
    /// child asks about its un-elevated parent. Best-effort like every other probe here:
    /// a failure returns <c>null</c> and the caller keeps the blocker.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    internal static string? TryGetProcessImagePath(uint processId)
    {
        if (processId == 0)
        {
            return null;
        }

        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, bInheritHandle: false, processId);
        if (handle == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            // Sized for an extended-length path so the call cannot fail for length
            // alone; this runs only for a reported blocker, never per registered file.
            var buffer = new char[MaxExtendedPathChars];
            var view = MemoryMarshal.Cast<char, ushort>(buffer.AsSpan());
            var size = (uint)buffer.Length;
            if (!QueryFullProcessImageNameW(handle, 0, ref view[0], ref size) || size == 0)
            {
                return null;
            }
            return new string(buffer, 0, (int)size);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>
    /// The existing files under the resolved install dir, deterministically ordered
    /// and capped. Empty for a fresh install (directory absent) or a blank path.
    /// </summary>
    private static string[] EnumerateExistingFiles(string? installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
        {
            return Array.Empty<string>();
        }
#pragma warning disable CA1031 // An unreadable tree simply yields no RM registration.
        try
        {
            var all = new List<string>();
            foreach (var f in Directory.EnumerateFiles(installDir, "*", SearchOption.AllDirectories))
            {
                all.Add(f);
                if (all.Count >= MaxRegisteredFiles)
                {
                    break;
                }
            }
            all.Sort(StringComparer.OrdinalIgnoreCase); // deterministic registration order
            return all.ToArray();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
#pragma warning restore CA1031
    }

    private static unsafe string ReadFixed(FixedAppName buffer)
    {
        var span = new ReadOnlySpan<char>(buffer.Value, CCH_RM_MAX_APP_NAME + 1);
        var nul = span.IndexOf('\0');
        return new string(nul >= 0 ? span[..nul] : span);
    }

    // --- rstrtmgr.dll interop -------------------------------------------------

    private const int CCH_RM_SESSION_KEY = 32;     // restartmanager.h
    private const int CCH_RM_MAX_APP_NAME = 255;
    private const int CCH_RM_MAX_SVC_NAME = 63;
    private const int ERROR_SUCCESS = 0;
    private const int ERROR_MORE_DATA = 234;
    private const uint SYNCHRONIZE = 0x00100000;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// <summary>Extended-length path ceiling (<c>\\?\</c> form), in chars.</summary>
    private const int MaxExtendedPathChars = 32768;

    // File-identity comparison (see SameFile / TryGetFileIdentity).
    private const uint FILE_READ_ATTRIBUTES = 0x0080;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint FILE_SHARE_DELETE = 0x00000004;
    private const uint OPEN_EXISTING = 3;
    private const uint VOLUME_NAME_DOS = 0x0;
    private const int FileIdInfo = 18;      // FILE_INFO_BY_HANDLE_CLASS.FileIdInfo
    private const int FileIdLength = 16;    // FILE_ID_128
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    /// <summary>
    /// <c>FILE_ID_INFO</c> — the volume serial plus a 128-bit file id. Kept fully
    /// blittable (a <c>fixed byte</c> buffer, not a marshalled array) so
    /// <c>[LibraryImport]</c> needs no runtime marshaller.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct FILE_ID_INFO
    {
        public ulong VolumeSerialNumber;
        public fixed byte FileId[FileIdLength];
    }

    // Every RM struct below is kept FULLY BLITTABLE (no bool, no marshalled types):
    // that is what lets [LibraryImport] pass the arrays with no runtime marshaller,
    // which is the Native-AOT requirement. Win32 BOOL is a 32-bit int, and FILETIME
    // is two DWORDs — spelled out here rather than reusing the ComTypes alias.
    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME_BLITTABLE
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public uint dwProcessId;
        public FILETIME_BLITTABLE ProcessStartTime;
    }

    // Fixed inline char buffers keep RM_PROCESS_INFO blittable, so [LibraryImport]
    // needs no runtime marshaller (Native-AOT clean).
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct FixedAppName
    {
        public fixed char Value[CCH_RM_MAX_APP_NAME + 1];
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct FixedSvcName
    {
        public fixed char Value[CCH_RM_MAX_SVC_NAME + 1];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        public FixedAppName strAppName;
        public FixedSvcName strServiceShortName;
        public uint ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        public int bRestartable; // Win32 BOOL — int keeps the struct blittable.
    }

    [SupportedOSPlatform("windows")]
    // R38: a `ref` into a caller-allocated char[CCH_RM_SESSION_KEY + 1], not a managed
    // string. strSessionKey is an OUT parameter and must never alias anything the
    // runtime may share. Declared as `ref ushort` (the blittable view of the UTF-16
    // buffer) because a `ref char` would demand DisableRuntimeMarshalling on the whole
    // assembly; the call site casts with MemoryMarshal and keeps the char[] typing.
    [LibraryImport("rstrtmgr.dll", EntryPoint = "RmStartSession")]
    private static partial int RmStartSession(out uint pSessionHandle, uint dwSessionFlags, ref ushort strSessionKey);

    [SupportedOSPlatform("windows")]
    [LibraryImport("rstrtmgr.dll", EntryPoint = "RmEndSession")]
    private static partial int RmEndSession(uint dwSessionHandle);

    [SupportedOSPlatform("windows")]
    [LibraryImport("rstrtmgr.dll", EntryPoint = "RmRegisterResources", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RmRegisterResources(
        uint dwSessionHandle,
        uint nFiles,
        [In] string[]? rgsFilenames,
        uint nApplications,
        [In] RM_UNIQUE_PROCESS[]? rgApplications,
        uint nServices,
        [In] string[]? rgsServiceNames);

    [SupportedOSPlatform("windows")]
    [LibraryImport("rstrtmgr.dll", EntryPoint = "RmGetList")]
    private static partial int RmGetList(
        uint dwSessionHandle,
        out uint pnProcInfoNeeded,
        ref uint pnProcInfo,
        [Out] RM_PROCESS_INFO[]? rgAffectedApps,
        out uint lpdwRebootReasons);

    [SupportedOSPlatform("windows")]
    [LibraryImport("rstrtmgr.dll", EntryPoint = "RmShutdown")]
    private static partial int RmShutdown(uint dwSessionHandle, uint lActionFlags, IntPtr fnStatus);

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", EntryPoint = "OpenMutexW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr OpenMutexW(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, string lpName);

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", EntryPoint = "OpenProcess", SetLastError = true)]
    private static partial IntPtr OpenProcess(
        uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [SupportedOSPlatform("windows")]
    // lpExeName is an OUT buffer and lpdwSize an in/out char count, so this takes a
    // `ref` into a caller-allocated char[] rather than a managed string — the same
    // discipline (and the same `ref ushort` spelling, since `ref char` would demand
    // DisableRuntimeMarshalling assembly-wide) as RmStartSession above.
    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageNameW(
        IntPtr hProcess, uint dwFlags, ref ushort lpExeName, ref uint lpdwSize);

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [SupportedOSPlatform("windows")]
    // The out buffer is a pointer rather than a `ref` to keep one signature usable for a
    // struct of any FILE_INFO_BY_HANDLE_CLASS; FILE_ID_INFO is blittable, so no runtime
    // marshaller is involved either way.
    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetFileInformationByHandleEx(
        IntPtr hFile, int fileInformationClass, void* lpFileInformation, uint dwBufferSize);

    [SupportedOSPlatform("windows")]
    // Same OUT-buffer discipline as RmStartSession / QueryFullProcessImageNameW above:
    // a `ref ushort` into a caller-allocated char[], never a managed string.
    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static partial uint GetFinalPathNameByHandleW(
        IntPtr hFile, ref ushort lpszFilePath, uint cchFilePath, uint dwFlags);

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hObject);
}
