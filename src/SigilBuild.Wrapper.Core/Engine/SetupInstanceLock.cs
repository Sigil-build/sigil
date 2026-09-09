namespace SigilBuild.Wrapper.Engine;

using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Cli;

/// <summary>
/// The setup's own single-instance guard (P6, gap G17) — the Inno <c>SetupMutex</c>
/// equivalent. The first process to <c>CreateMutexW</c> the app+scope-derived name
/// owns the install; a second simultaneous launch sees the name already taken and
/// bails out with a friendly notice (wizard) or a dedicated exit code (silent),
/// leaving the first instance completely untouched.
/// </summary>
/// <remarks>
/// <para>
/// <b>Acquire AFTER the elevation decision.</b> A per-machine install relaunches
/// itself elevated and the parent blocks on the child; if the parent held the mutex
/// the elevated child would see itself as a "second instance". Both entry points
/// therefore take the lock only once the relaunch branch has been passed, i.e. in
/// the process that actually performs the install.
/// </para>
/// <para>
/// A machine-scope install uses the <c>Global\</c> namespace so it is exclusive
/// across terminal-server sessions (two admins cannot install concurrently); a
/// per-user install uses <c>Local\</c>, so a per-user install in another session is
/// independent. The handle is released on dispose and, regardless, by the OS when
/// the process exits — so a crashed setup never leaves the name stuck.
/// </para>
/// <para>
/// <b>The one admitted exception (R76).</b> A P3 upgrade removes the previous version
/// by running ITS <c>uninstall.exe /S /Uninstall &lt;scope&gt;</c> — a child process that
/// derives the same app+scope name and, until R76, was refused as a second instance,
/// failing every unelevated per-user upgrade with exit 5 and installing nothing. The
/// installer therefore hands that one child an explicit handoff naming itself; the
/// child admits it only after the OS confirms what it can confirm. Both scopes are
/// covered: the elevated machine-scope shape is the same code with a <c>Global\</c>
/// name. What that check is and is not proof of is set out on
/// <see cref="HandoffAdmits"/>, and the short version belongs here too: it does NOT
/// establish that the minter holds the mutex — only that our real parent asserts it
/// spawned us for a teardown of this one app+scope, so what gets admitted is an
/// uninstall run under the same user, which already owns the state this guard
/// protects. A second Setup.exe launched BY HAND still meets
/// <see cref="SetupLockRefusal.AnotherInstanceRunning"/> and still exits 5; an install
/// is refused even with a valid token; and R34's fail-closed
/// <see cref="SetupLockRefusal.NameNotAvailable"/> branch is not rescuable by any
/// handoff.
/// </para>
/// </remarks>
public sealed partial class SetupInstanceLock : IDisposable
{
    private IntPtr _handle;
    private readonly bool _owns;

    private SetupInstanceLock(IntPtr handle, string name, bool owns)
    {
        _handle = handle;
        Name = name;
        _owns = owns;
    }

    /// <summary>The mutex name this lock owns (diagnostics / tests).</summary>
    public string Name { get; }

    /// <summary>
    /// True when this process CREATED the name and therefore owns the guard, as
    /// opposed to holding a sentinel (non-Windows, <see cref="SetupLockRefusal.GuardUnavailable"/>)
    /// or having been admitted under a parent installer's lock
    /// (<see cref="SetupLockRefusal.AdmittedByParentInstaller"/>). Only an owner may
    /// mint a handoff for a child (see <see cref="MintChildHandoff"/>).
    /// </summary>
    internal bool OwnsTheGuard => _owns && _handle != IntPtr.Zero;

    /// <summary>
    /// The mutex name for <paramref name="appId"/> at <paramref name="scope"/>.
    /// Machine scope is <c>Global\</c> (cross-session exclusive), user scope is
    /// <c>Local\</c>.
    /// </summary>
    public static string NameFor(string appId, InstallScope scope)
    {
        var ns = scope == InstallScope.Machine ? "Global" : "Local";
        return $"{ns}\\sigil-setup-{Sanitize(appId)}-{(scope == InstallScope.Machine ? "machine" : "user")}";
    }

    /// <summary>
    /// Why <see cref="TryAcquire(string, InstallScope, out SetupLockRefusal)"/> returned
    /// no lock, or how the one it returned was obtained. Distinct values because the
    /// three are three different situations and used to be one (R34).
    /// </summary>
    public enum SetupLockRefusal
    {
        /// <summary>The lock was taken and this process owns the name.</summary>
        None = 0,

        /// <summary>
        /// <c>ERROR_ALREADY_EXISTS</c> — another setup for this app+scope is running.
        /// The ordinary, expected contention case.
        /// </summary>
        AnotherInstanceRunning = 1,

        /// <summary>
        /// The name exists but is not ours to take: <c>CreateMutexW</c> failed and the
        /// object is there — either a mutex whose DACL denies us
        /// (<c>ERROR_ACCESS_DENIED</c>) or, more cheaply for an attacker, a DIFFERENT
        /// kind of kernel object squatting the name (<c>ERROR_INVALID_HANDLE</c>). Both
        /// mean the guard cannot be established, so this fails closed exactly like
        /// contention rather than proceeding without a guard.
        /// </summary>
        NameNotAvailable = 2,

        /// <summary>
        /// The name does NOT exist and we still could not create it — the
        /// <c>Global\</c> namespace from a process without
        /// <c>SeCreateGlobalPrivilege</c>, which is what a machine-scope
        /// <c>/Update</c> (the one path that never self-elevates) looks like. There is
        /// no second instance to protect against here, so a sentinel is returned and the
        /// run proceeds UNGUARDED — but the caller is told, instead of the old silent
        /// pretence that a lock was held.
        /// </summary>
        GuardUnavailable = 3,

        /// <summary>
        /// R76 — the name is held, and this process's REAL parent asserted, with a token
        /// naming this exact guard, that it spawned us for the P3 upgrade teardown.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Say exactly what that is worth.</b> Nothing here proves the minter is the
        /// process HOLDING the mutex — a named mutex has existence, not an owner
        /// identity, so a process holding no lock at all can mint a token for its own
        /// child while a different process holds the name. What the checks do establish
        /// is that the admitted process is an <em>uninstall</em> run, spawned by a live
        /// parent that named this one app+scope, under the same user — the user that
        /// already owns the install directory, the state store and the ARP row this
        /// guard protects, and can corrupt them directly without any of this. A second
        /// Setup.exe launched BY HAND still meets
        /// <see cref="AnotherInstanceRunning"/> and still exits 5, and an
        /// <see cref="Cli.WrapperMode.Install"/> run is refused even holding a valid
        /// token. In the real path the parent is blocked in <c>WaitForExit</c> for the
        /// child's whole duration, so the teardown runs inside its critical section.
        /// The full analysis is on <see cref="HandoffAdmits"/>.
        /// </para>
        /// </remarks>
        AdmittedByParentInstaller = 4,
    }

    /// <summary>
    /// R76 — the environment variable an installer sets on the PRIOR VERSION's
    /// <c>uninstall.exe</c> it spawns for the upgrade teardown, naming itself as the
    /// holder of the guard the child is about to contend with. Set on the child's
    /// environment block only (<c>ProcessStartInfo.Environment</c>), never on this
    /// process's own, and consumed (cleared) by the child before it does anything else
    /// so it is not inherited any further down the tree.
    /// </summary>
    internal const string HandoffVariable = "SIGIL_SETUP_LOCK_HANDOFF";

    /// <summary>The only handoff wire format this build mints or accepts.</summary>
    private const string HandoffVersion = "1";

    /// <summary>
    /// Take the single-instance lock for this app+scope. Returns the owning lock, or
    /// <c>null</c> when the name is taken. Off Windows there is no contention model, so
    /// a non-owning sentinel lock is always returned.
    /// </summary>
    public static SetupInstanceLock? TryAcquire(string appId, InstallScope scope)
        => TryAcquire(appId, scope, out _);

    /// <summary>
    /// As <see cref="TryAcquire(string, InstallScope)"/>, reporting <em>which</em> branch
    /// was taken so the caller can log it and pick the right message (R34).
    /// </summary>
    public static SetupInstanceLock? TryAcquire(
        string appId, InstallScope scope, out SetupLockRefusal refusal)
        => TryAcquire(appId, scope, WrapperMode.Install, handoff: null, out refusal);

    /// <summary>
    /// As <see cref="TryAcquire(string, InstallScope, out SetupLockRefusal)"/>, but able
    /// to accept the R76 parent <paramref name="handoff"/> — which is why it also needs
    /// the run's <paramref name="mode"/>. BOTH entry points must call this overload; the
    /// shorter ones exist for callers that can never be a teardown child.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The handoff is honoured for <see cref="WrapperMode.Uninstall"/> ONLY. That is
    /// what the parent spawns (<c>uninstall.exe /S /Uninstall &lt;scope&gt;</c>), and
    /// restricting it there means no handoff — valid, stale or forged — can ever admit a
    /// second concurrent <em>install</em> of an application.
    /// </para>
    /// <para>
    /// The token is passed IN rather than read here, because the entry point must
    /// <see cref="ConsumeHandoffToken"/> before the elevation branch — i.e. before this
    /// process can spawn anything at all — and this call happens after it. Passing it
    /// through keeps "read once, clear immediately" a property of the process rather
    /// than of this method.
    /// </para>
    /// </remarks>
    public static SetupInstanceLock? TryAcquire(
        string appId, InstallScope scope, WrapperMode mode, string? handoff,
        out SetupLockRefusal refusal)
    {
        var name = NameFor(appId, scope);
        if (!OperatingSystem.IsWindows())
        {
            refusal = SetupLockRefusal.None;
            return new SetupInstanceLock(IntPtr.Zero, name, owns: false);
        }

        return TryAcquireWindows(
            name, mode == WrapperMode.Uninstall ? handoff : null, out refusal);
    }

    [SupportedOSPlatform("windows")]
    private static SetupInstanceLock? TryAcquireWindows(
        string name, string? handoff, out SetupLockRefusal refusal)
    {
        // bInitialOwner: false — ownership is irrelevant; existence is the signal.
        var handle = CreateMutexW(IntPtr.Zero, bInitialOwner: false, name);
        var lastError = Marshal.GetLastWin32Error();

        if (handle == IntPtr.Zero)
        {
            // R34. This branch used to return a non-owning sentinel indistinguishable
            // from a real lock, so two installs could run concurrently — and the name is
            // fully derivable from the public app id, so producing this branch on demand
            // was a same-user, no-privilege operation: create ANY other kind of kernel
            // object (an event, a semaphore) under the name and CreateMutexW fails
            // forever after.
            //
            // The fix is not "fail closed on every failure", which would break the one
            // legitimate case: a machine-scope /Update never self-elevates, so it asks
            // for a Global\ name without SeCreateGlobalPrivilege and is denied. That is
            // ACCESS_DENIED too, so the error code alone cannot separate "the name is
            // taken" from "we may not create names here". Asking whether the object
            // EXISTS separates them exactly.
            if (NameIsTaken(name))
            {
                // R76 does NOT reach here on purpose: a handoff never rescues this
                // branch. "The name exists but CreateMutexW failed" is R34's squatted /
                // DACL-denied case, where we cannot even establish that the object is
                // the guard — let alone that our parent holds it. Fail closed.
                refusal = SetupLockRefusal.NameNotAvailable;
                return null;
            }

            // Nothing is there; we simply could not create it. Proceed, unguarded and
            // said out loud.
            refusal = SetupLockRefusal.GuardUnavailable;
            return new SetupInstanceLock(IntPtr.Zero, name, owns: false);
        }

        if (lastError == ERROR_ALREADY_EXISTS)
        {
            // R76: the holder may be the installer that spawned US for the upgrade
            // teardown. `handle` is a valid second reference to the very mutex the
            // parent holds, so an admitted child keeps it (the parent's own reference
            // is what keeps the name exclusive against strangers; this one is dropped
            // on Dispose).
            if (HandoffAdmits(handoff, name))
            {
                refusal = SetupLockRefusal.AdmittedByParentInstaller;
                return new SetupInstanceLock(handle, name, owns: false);
            }

            // Someone else owns the install: release our reference and report.
            CloseHandle(handle);
            refusal = SetupLockRefusal.AnotherInstanceRunning;
            return null;
        }

        refusal = SetupLockRefusal.None;
        return new SetupInstanceLock(handle, name, owns: true);
    }

    // ── R76: the prior-uninstaller handoff ───────────────────────────────────

    /// <summary>
    /// R76 — the token to put on the environment of the PRIOR VERSION's
    /// <c>uninstall.exe</c> this process is about to spawn for the P3 upgrade teardown,
    /// or <c>null</c> when this process does not own the guard (in which case there is
    /// nothing to hand over and the child must contend normally — fail closed).
    /// </summary>
    internal string? MintChildHandoff()
    {
        if (!OwnsTheGuard || !OperatingSystem.IsWindows())
        {
            return null;
        }
        var self = (uint)Environment.ProcessId;
        return TryGetProcessCreationTime(self, out var created)
            ? FormatHandoff(self, created, Name)
            : null;
    }

    /// <summary>The handoff wire format. Shared by the minter and the tests.</summary>
    internal static string FormatHandoff(uint parentPid, long parentCreationTime, string lockName)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{HandoffVersion}|{parentPid}|{parentCreationTime}|{lockName}");

    /// <summary>
    /// Read the handoff token out of the environment and REMOVE it, so that whatever
    /// this process goes on to spawn does not inherit an admission it was never given.
    /// </summary>
    /// <remarks>
    /// Both entry points call this ONCE, at the top of <c>Main</c>, <b>before</b> the
    /// self-elevation branch — the first thing either of them can spawn is the elevated
    /// relaunch of itself, and a token still sitting in the environment at that point
    /// would be a token the parent never meant to hand to that child. (It could not
    /// admit it today, because an elevated relaunch only happens for a machine-scope run
    /// whose guard name differs from the user-scope one the token would name — but that
    /// is a fact about two other pieces of code, and not one worth depending on.) Not
    /// platform-gated: the environment is the environment everywhere.
    /// </remarks>
    internal static string? ConsumeHandoffToken()
    {
        var token = Environment.GetEnvironmentVariable(HandoffVariable);
        if (!string.IsNullOrEmpty(token))
        {
            Environment.SetEnvironmentVariable(HandoffVariable, null);
        }
        return token;
    }

    /// <summary>
    /// R76 — does <paramref name="token"/> prove that the process that spawned us is the
    /// installer holding <paramref name="lockName"/>, so that we may run inside its
    /// critical section instead of being refused as a second instance?
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a token at all.</b> A per-user v1 → v2 upgrade held the app+scope guard and
    /// then spawned the prior version's <c>uninstall.exe</c>, which derived the SAME name
    /// and bailed with exit 5 — the installer's own child counted as a stranger, and
    /// every unelevated upgrade and forced downgrade failed with nothing installed. The
    /// child cannot tell "my parent holds this" from "a stranger holds this" by looking
    /// at the mutex: a named mutex has existence, not an owner identity. So the parent
    /// says so explicitly, and this method checks the claim against facts only the OS
    /// can answer.
    /// </para>
    /// <para>
    /// <b>All four must hold</b>, and any one of them failing refuses (fail closed):
    /// </para>
    /// <list type="number">
    /// <item><b>The lock name matches ours exactly.</b> This is the property that keeps a
    /// planted token from touching anything but the one app+scope the minter named: a
    /// token for <c>com.acme.A</c> cannot admit a setup for <c>com.acme.B</c>, and a
    /// user-scope token cannot admit a machine-scope run. Compared case-insensitively
    /// because the object manager's namespace is.</item>
    /// <item><b>The named pid is our REAL parent</b>, read from the OS
    /// (<see cref="TryGetParentProcessId"/>), never from the token. A token is therefore
    /// non-transferable: leaked, logged, or inherited by a grandchild, it admits nobody,
    /// because only the process the parent itself created has that ppid.</item>
    /// <item><b>That parent is still alive and is the SAME process instance</b> — its
    /// creation time must equal the one in the token. A recorded ppid is just a number:
    /// once the parent exits the number can be reused, and a stale token must not be
    /// resurrected by a new process that happens to inherit the pid.</item>
    /// <item><b>We are the uninstall teardown</b>, enforced by the caller
    /// (<see cref="TryAcquire(string, InstallScope, WrapperMode, out SetupLockRefusal)"/>):
    /// no token can ever admit a concurrent <em>install</em>.</item>
    /// </list>
    /// <para>
    /// <b>What this deliberately does NOT claim.</b> The token carries no secret — pid,
    /// creation time and the guard name are all public — and secrecy would buy nothing,
    /// because the appId that derives the name is public too. Its strength is the
    /// binding above, not confidentiality. And the binding stops short of one thing
    /// worth naming precisely, because a mutex cannot answer it: <b>the minter is never
    /// shown to be the HOLDER</b>. A process holding no lock at all can mint a token for
    /// its own child while some other process holds the name, and that child — if it is
    /// an uninstall of that same app+scope — is admitted. So the residual is: a process
    /// that itself launches a setup process can name ITSELF as the parent and get its
    /// own uninstall child admitted alongside a running install of that one app. In user
    /// scope that grants nothing —
    /// the attacker is the same user, who already owns the install directory, the state
    /// store and the HKCU ARP row, and can corrupt them directly (the same reasoning as
    /// <c>InstallSession.PriorUninstallerNeedsTrust</c>). Across the privilege boundary
    /// it is not reachable: to be the real parent of an ELEVATED uninstaller an
    /// unprivileged attacker needs UAC consent for the application's own uninstaller,
    /// and re-parenting onto an elevated process requires <c>PROCESS_CREATE_PROCESS</c>
    /// on it, which a lower integrity level does not get. The guard's purpose — stopping
    /// two installs racing on one app's state, including any OTHER app's — is intact in
    /// both scopes.
    /// </para>
    /// </remarks>
    [SupportedOSPlatform("windows")]
    internal static bool HandoffAdmits(string? token, string lockName)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        // version | parent pid | parent creation time (FILETIME) | guard name
        var parts = token.Split('|', 4);
        if (parts.Length != 4
            || !string.Equals(parts[0], HandoffVersion, StringComparison.Ordinal)
            || !uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
            || pid == 0
            || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var created)
            || created <= 0
            || !string.Equals(parts[3], lockName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return TryGetParentProcessId(out var parentPid)
            && parentPid == pid
            && TryGetProcessCreationTime(pid, out var actualCreated)
            && actualCreated == created;
    }

    /// <summary>
    /// This process's parent pid, from a process snapshot. <c>false</c> when it cannot
    /// be determined — which refuses the handoff.
    /// </summary>
    /// <remarks>
    /// Toolhelp rather than <c>NtQueryInformationProcess</c>: <c>PROCESSENTRY32W</c> is
    /// documented and stable, and this runs once per process at most.
    /// <c>CreateToolhelp32Snapshot</c> can fail with <c>ERROR_BAD_LENGTH</c> while the
    /// process list changes under it, which is why it is retried.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    internal static unsafe bool TryGetParentProcessId(out uint parentPid)
    {
        parentPid = 0;
        var self = (uint)Environment.ProcessId;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snapshot == INVALID_HANDLE_VALUE)
            {
                continue;
            }
            try
            {
                var entry = default(PROCESSENTRY32W);
                entry.dwSize = (uint)sizeof(PROCESSENTRY32W);
                for (var more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
                {
                    if (entry.th32ProcessID == self)
                    {
                        parentPid = entry.th32ParentProcessID;
                        return parentPid != 0;
                    }
                }
            }
            finally
            {
                CloseHandle(snapshot);
            }
        }
        return false;
    }

    /// <summary>
    /// The creation time (raw FILETIME) of a LIVE process, or <c>false</c> when the
    /// process is gone or cannot be opened. Both sides of the handoff read it through
    /// this one call, so the values compare bit-for-bit.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static bool TryGetProcessCreationTime(uint processId, out long creationTime)
    {
        creationTime = 0;
        if (processId == 0)
        {
            return false;
        }
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, bInheritHandle: false, processId);
        if (handle == IntPtr.Zero)
        {
            return false;
        }
        try
        {
            return GetProcessTimes(handle, out creationTime, out _, out _, out _);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>
    /// True when a kernel object already occupies <paramref name="name"/> — whether it
    /// is a mutex we are denied access to, or some other object type squatting the name.
    /// </summary>
    /// <remarks>
    /// <c>OpenMutexW</c> reports <c>ERROR_FILE_NOT_FOUND</c> only when the name is
    /// genuinely free; <c>ERROR_ACCESS_DENIED</c> (a mutex with a hostile DACL) and
    /// <c>ERROR_INVALID_HANDLE</c> (the name is an event/semaphore/section) both mean it
    /// is occupied.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    private static bool NameIsTaken(string name)
    {
        var probe = OpenMutexW(SYNCHRONIZE, bInheritHandle: false, name);
        if (probe != IntPtr.Zero)
        {
            CloseHandle(probe);
            return true;
        }
        return Marshal.GetLastWin32Error() != ERROR_FILE_NOT_FOUND;
    }

    // Reduce the AppId to a mutex-name-safe segment (no backslashes — they would
    // create an unintended namespace).
    private static string Sanitize(string appId)
    {
        var sb = new System.Text.StringBuilder(appId.Length);
        foreach (var c in appId)
        {
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_');
        }
        var s = sb.ToString();
        return s.Length == 0 ? "app" : s;
    }

    public void Dispose()
    {
        var h = _handle;
        _handle = IntPtr.Zero;
        if (h != IntPtr.Zero && OperatingSystem.IsWindows())
        {
            CloseHandle(h);
        }
    }

    private const int ERROR_ALREADY_EXISTS = 183;
    private const int ERROR_FILE_NOT_FOUND = 2;
    private const uint SYNCHRONIZE = 0x00100000;

    // R76 handoff verification.
    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int MAX_PATH = 260;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    /// <summary>
    /// <c>tlhelp32.h</c>'s <c>PROCESSENTRY32W</c>. The inline <c>szExeFile</c> buffer is
    /// unread but must be declared: <c>dwSize</c> is validated against the real struct
    /// size, and the OS writes the name into it. A <c>fixed</c> buffer keeps the struct
    /// blittable so <c>[LibraryImport]</c> needs no runtime marshaller (Native-AOT
    /// clean) — the same idiom as <c>FilesInUse</c>'s RM structs.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public nuint th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        public fixed char szExeFile[MAX_PATH];
    }

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(
        uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(
        IntPtr hProcess, out long lpCreationTime, out long lpExitTime, out long lpKernelTime, out long lpUserTime);

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", EntryPoint = "CreateMutexW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr CreateMutexW(IntPtr lpMutexAttributes, [MarshalAs(UnmanagedType.Bool)] bool bInitialOwner, string lpName);

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", EntryPoint = "OpenMutexW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr OpenMutexW(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, string lpName);

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hObject);
}
