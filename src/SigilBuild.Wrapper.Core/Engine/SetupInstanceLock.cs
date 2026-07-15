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
    /// Take the single-instance lock for this app+scope. Returns the owning lock, or
    /// <c>null</c> when another setup instance already holds it. Off Windows there is
    /// no contention model, so a non-owning sentinel lock is always returned.
    /// </summary>
    public static SetupInstanceLock? TryAcquire(string appId, InstallScope scope)
    {
        var name = NameFor(appId, scope);
        if (!OperatingSystem.IsWindows())
        {
            return new SetupInstanceLock(IntPtr.Zero, name);
        }
        return TryAcquireWindows(name);
    }

    [SupportedOSPlatform("windows")]
    private static SetupInstanceLock? TryAcquireWindows(string name)
    {
        // bInitialOwner: false — ownership is irrelevant; existence is the signal.
        var handle = CreateMutexW(IntPtr.Zero, bInitialOwner: false, name);
        var lastError = Marshal.GetLastWin32Error();

        if (handle == IntPtr.Zero)
        {
            // Could not create the name at all (e.g. ACL on Global\ from a
            // non-elevated process). Fail open: a missing guard is better than a
            // setup that refuses to run.
            return new SetupInstanceLock(IntPtr.Zero, name);
        }

        if (lastError == ERROR_ALREADY_EXISTS)
        {
            // Someone else owns the install: release our reference and report.
            CloseHandle(handle);
            return null;
        }

        return new SetupInstanceLock(handle, name);
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

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", EntryPoint = "CreateMutexW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr CreateMutexW(IntPtr lpMutexAttributes, [MarshalAs(UnmanagedType.Bool)] bool bInitialOwner, string lpName);

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hObject);
}
