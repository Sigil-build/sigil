namespace SigilBuild.Wrapper.Engine;

using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SigilBuild.Core.Manifest;

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
/// </remarks>
public sealed partial class SetupInstanceLock : IDisposable
{
    private IntPtr _handle;

    private SetupInstanceLock(IntPtr handle, string name)
    {
        _handle = handle;
        Name = name;
    }

    /// <summary>The mutex name this lock owns (diagnostics / tests).</summary>
    public string Name { get; }

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
    }

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
    {
        var name = NameFor(appId, scope);
        if (!OperatingSystem.IsWindows())
        {
            refusal = SetupLockRefusal.None;
            return new SetupInstanceLock(IntPtr.Zero, name);
        }
        return TryAcquireWindows(name, out refusal);
    }

    [SupportedOSPlatform("windows")]
    private static SetupInstanceLock? TryAcquireWindows(string name, out SetupLockRefusal refusal)
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
                refusal = SetupLockRefusal.NameNotAvailable;
                return null;
            }

            // Nothing is there; we simply could not create it. Proceed, unguarded and
            // said out loud.
            refusal = SetupLockRefusal.GuardUnavailable;
            return new SetupInstanceLock(IntPtr.Zero, name);
        }

        if (lastError == ERROR_ALREADY_EXISTS)
        {
            // Someone else owns the install: release our reference and report.
            CloseHandle(handle);
            refusal = SetupLockRefusal.AnotherInstanceRunning;
            return null;
        }

        refusal = SetupLockRefusal.None;
        return new SetupInstanceLock(handle, name);
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
