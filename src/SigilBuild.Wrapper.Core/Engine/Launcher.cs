namespace SigilBuild.Wrapper.Engine;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

/// <summary>
/// Launches the <c>run_after_install</c> target (P2, gap G4) <b>unelevated</b>.
/// </summary>
/// <remarks>
/// When the installer process is not elevated (a per-user install), a normal
/// spawn already runs at the user's medium integrity. When the installer is
/// running elevated (a per-machine install relaunched via UAC), a normal spawn
/// would hand the launched app the <em>admin</em> token — wrong, and a security
/// smell. In that case we de-elevate by launching under the desktop shell's
/// (Explorer's) medium-integrity primary token via <c>CreateProcessWithTokenW</c>,
/// the canonical "run as the logged-on user from an elevated process" technique.
/// All interop is source-generated <c>[LibraryImport]</c> (Native-AOT safe).
/// Best-effort throughout: a launch failure is never fatal to the install.
/// </remarks>
public static partial class Launcher
{
    /// <summary>
    /// Launch <paramref name="path"/> with <paramref name="args"/> unelevated.
    /// Returns true when the process was started. Never throws.
    /// </summary>
    public static bool LaunchUnelevated(string path, IReadOnlyList<string>? args)
        => Launch(path, args) == LaunchOutcome.Started;

    /// <summary>
    /// As <see cref="LaunchUnelevated"/>, reporting <em>why</em> nothing started so the
    /// caller can log a de-elevation refusal rather than an anonymous <c>false</c> (R29).
    /// </summary>
    public static LaunchOutcome Launch(string path, IReadOnlyList<string>? args)
        => LaunchCore(
            path,
            args,
            () => OperatingSystem.IsWindows() && Elevation.IsProcessElevated(),
            static (p, a) => TryLaunchViaShellTokenOnWindows(p, a),
            TryLaunchDirect);

    /// <summary>
    /// The launch decision, with its three effects injected so the elevated branch is
    /// testable off an elevated runner. The production entry point is
    /// <see cref="Launch"/>.
    /// </summary>
    /// <remarks>
    /// <strong>R29 — there is no fall-through from the elevated branch, and there must
    /// not be one.</strong> This used to try the de-elevated launch and, on any failure,
    /// call <see cref="TryLaunchDirect"/> anyway — which hands the launched application
    /// the INSTALLER'S ADMIN TOKEN, with no log line and no user-visible signal. That is
    /// the exact bug class this whole class exists to prevent, and it silently undid
    /// P2's own acceptance criterion ("launch checkbox starts the app unelevated"). The
    /// user losing a convenience launch is a smaller harm than the user's editor, browser
    /// or game silently inheriting administrator rights for the rest of its lifetime, so
    /// the launch is skipped and reported.
    /// </remarks>
    internal static LaunchOutcome LaunchCore(
        string path,
        IReadOnlyList<string>? args,
        Func<bool> isElevated,
        Func<string, IReadOnlyList<string>?, bool> deElevatedLaunch,
        Func<string, IReadOnlyList<string>?, bool> directLaunch)
    {
        ArgumentNullException.ThrowIfNull(isElevated);
        ArgumentNullException.ThrowIfNull(deElevatedLaunch);
        ArgumentNullException.ThrowIfNull(directLaunch);

        if (string.IsNullOrWhiteSpace(path))
        {
            return LaunchOutcome.NothingToLaunch;
        }

        if (isElevated())
        {
            // Explorer's medium-integrity primary token via CreateProcessWithTokenW.
            // Succeed here or do not launch at all.
            return deElevatedLaunch(path, args)
                ? LaunchOutcome.Started
                : LaunchOutcome.SkippedDeElevationUnavailable;
        }

        // Not elevated: a plain spawn already runs at the user's own integrity level.
        return directLaunch(path, args) ? LaunchOutcome.Started : LaunchOutcome.StartFailed;
    }

    private static bool TryLaunchViaShellTokenOnWindows(string path, IReadOnlyList<string>? args)
        => OperatingSystem.IsWindows() && TryLaunchViaShellToken(path, args);

    private static bool TryLaunchDirect(string path, IReadOnlyList<string>? args)
    {
#pragma warning disable CA1031 // Best-effort launch: any spawn failure just returns false.
        try
        {
            var psi = new ProcessStartInfo { FileName = path, UseShellExecute = false };
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                psi.WorkingDirectory = dir;
            }
            if (args is not null)
            {
                foreach (var a in args)
                {
                    psi.ArgumentList.Add(a);
                }
            }
            using var proc = Process.Start(psi);
            return proc is not null;
        }
        catch (Exception)
        {
            return false;
        }
#pragma warning restore CA1031
    }

    [SupportedOSPlatform("windows")]
    private static bool TryLaunchViaShellToken(string path, IReadOnlyList<string>? args)
    {
        var shell = GetShellWindow();
        if (shell == IntPtr.Zero)
        {
            return false;
        }

        _ = GetWindowThreadProcessId(shell, out var pid);
        if (pid == 0)
        {
            return false;
        }

        var hProc = OpenProcess(PROCESS_QUERY_INFORMATION, bInheritHandle: false, pid);
        if (hProc == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            if (!OpenProcessToken(hProc, TOKEN_DUPLICATE, out var hToken))
            {
                return false;
            }

            try
            {
                if (!DuplicateTokenEx(hToken, TOKEN_ALL_ACCESS, IntPtr.Zero,
                        SecurityImpersonation, TokenPrimary, out var hPrimary))
                {
                    return false;
                }

                try
                {
                    return CreateProcessWithShellToken(hPrimary, path, args);
                }
                finally
                {
                    CloseHandle(hPrimary);
                }
            }
            finally
            {
                CloseHandle(hToken);
            }
        }
        finally
        {
            CloseHandle(hProc);
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool CreateProcessWithShellToken(IntPtr primaryToken, string path, IReadOnlyList<string>? args)
    {
        var full = new List<string>(1 + (args?.Count ?? 0)) { path };
        if (args is not null)
        {
            full.AddRange(args);
        }
        var commandLine = Elevation.BuildCommandLine(full);

        var workingDir = Path.GetDirectoryName(path);

        var pApp = Marshal.StringToHGlobalUni(path);
        var pCmd = Marshal.StringToHGlobalUni(commandLine);
        var pDir = string.IsNullOrEmpty(workingDir) ? IntPtr.Zero : Marshal.StringToHGlobalUni(workingDir);
        try
        {
            var si = new STARTUPINFOW
            {
                cb = (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<STARTUPINFOW>(),
            };

            var ok = CreateProcessWithTokenW(
                primaryToken, dwLogonFlags: 0, pApp, pCmd, dwCreationFlags: 0,
                IntPtr.Zero, pDir, ref si, out var pi);

            if (!ok)
            {
                return false;
            }

            // We don't wait on the launched app — close the returned handles.
            if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
            if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(pApp);
            Marshal.FreeHGlobal(pCmd);
            if (pDir != IntPtr.Zero) Marshal.FreeHGlobal(pDir);
        }
    }

    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint TOKEN_DUPLICATE = 0x0002;
    private const uint TOKEN_ALL_ACCESS = 0xF01FF;
    private const int SecurityImpersonation = 2; // SECURITY_IMPERSONATION_LEVEL
    private const int TokenPrimary = 1;          // TOKEN_TYPE

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOW
    {
        public uint cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public uint dwX;
        public uint dwY;
        public uint dwXSize;
        public uint dwYSize;
        public uint dwXCountChars;
        public uint dwYCountChars;
        public uint dwFillAttribute;
        public uint dwFlags;
        public ushort wShowWindow;
        public ushort cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [SupportedOSPlatform("windows")]
    [LibraryImport("user32.dll")]
    private static partial IntPtr GetShellWindow();

    [SupportedOSPlatform("windows")]
    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [SupportedOSPlatform("windows")]
    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [SupportedOSPlatform("windows")]
    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DuplicateTokenEx(
        IntPtr hExistingToken, uint dwDesiredAccess, IntPtr lpTokenAttributes,
        int impersonationLevel, int tokenType, out IntPtr phNewToken);

    [SupportedOSPlatform("windows")]
    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcessWithTokenW(
        IntPtr hToken, uint dwLogonFlags, IntPtr lpApplicationName, IntPtr lpCommandLine,
        uint dwCreationFlags, IntPtr lpEnvironment, IntPtr lpCurrentDirectory,
        ref STARTUPINFOW lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}

/// <summary>
/// What <see cref="Launcher.Launch"/> did (R29). <see cref="Started"/> is the only value
/// that means a process exists.
/// </summary>
public enum LaunchOutcome
{
    /// <summary>The process was started, at the user's own integrity level.</summary>
    Started = 0,

    /// <summary>No <c>run_after_install</c> target was configured.</summary>
    NothingToLaunch = 1,

    /// <summary>An unelevated spawn was attempted and the process would not start.</summary>
    StartFailed = 2,

    /// <summary>
    /// The installer is elevated and de-elevation was unavailable — no desktop shell, or
    /// its token could not be duplicated. The launch was SKIPPED. Launching anyway would
    /// have handed the application the installer's administrator token.
    /// </summary>
    SkippedDeElevationUnavailable = 3,
}
