namespace SigilBuild.Wrapper.Engine;

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

/// <summary>
/// A single thing blocking the install / uninstall (P6, gap G7): either a declared
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
/// Files-in-use detection (P6, gap G7) — the Inno <c>AppMutex</c> +
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
/// All interop is source-generated <c>[LibraryImport]</c> (Native-AOT safe). Every
/// probe is best-effort: on any RM failure the sweep reports no blockers rather than
/// failing the install — a false "clear" degrades to the pre-P6 behaviour (the step
/// engine surfaces the locked-file error and rolls back), whereas a false "blocked"
/// would wedge a perfectly good install.
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
    /// Restart Manager. Returns every distinct blocker found (empty = clear). Never
    /// throws; always empty off Windows.
    /// </summary>
    public static IReadOnlyList<AppBlocker> Scan(IReadOnlyList<string>? appMutexes, string? installDir)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<AppBlocker>();
        }

        var found = new List<AppBlocker>();
        found.AddRange(ScanMutexes(appMutexes));
        found.AddRange(ScanRestartManager(installDir));

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
    private static List<AppBlocker> ScanRestartManager(string? installDir)
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
            blockers.AddRange(GetList(session));
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
        // R38: strSessionKey is an OUT buffer — RmStartSession writes
        // CCH_RM_SESSION_KEY chars plus a NUL into it. This used to hand it a managed
        // `string`, which [LibraryImport]'s UTF-16 marshalling pins in place, so the API
        // wrote through the string's own buffer. The size was exact and
        // `new string(char, count)` is never interned, so nothing overflowed and nothing
        // shared was corrupted today — but one refactor to a literal or a cached
        // constant would have silently mutated an INTERNED string, and the signature was
        // advertising "in" for a parameter the API treats as "out". A char[] behind a
        // `ref char` signature says what actually happens and cannot be aliased.
        var key = new char[CCH_RM_SESSION_KEY + 1];
        var buffer = MemoryMarshal.Cast<char, ushort>(key.AsSpan());
        return RmStartSession(out session, 0, ref buffer[0]) == 0;
    }

    [SupportedOSPlatform("windows")]
    private static List<AppBlocker> GetList(uint session)
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
            var name = ReadFixed(infos[i].strAppName);
            if (string.IsNullOrWhiteSpace(name))
            {
                name = $"pid {infos[i].Process.dwProcessId}";
            }
            blockers.Add(new AppBlocker(name, infos[i].Process.dwProcessId, FromMutex: false));
        }
        return blockers;
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
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hObject);
}
