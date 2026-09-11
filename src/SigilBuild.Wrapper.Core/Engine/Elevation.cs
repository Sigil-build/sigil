namespace SigilBuild.Wrapper.Engine;

using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

/// <summary>
/// Self-elevation for a per-machine install. The host exe ships
/// with a <c>requestedExecutionLevel level="asInvoker"</c> manifest so a per-user
/// install never triggers UAC. When the resolved scope is
/// <see cref="SigilBuild.Core.Manifest.InstallScope.Machine"/> and the current
/// process is <em>not</em> elevated, the entry point relaunches <em>itself</em>
/// elevated via <c>ShellExecuteExW</c> with the <c>runas</c> verb — forwarding
/// every original argument — waits for the elevated child, and propagates its
/// exit code as this process's exit code.
/// </summary>
/// <remarks>
/// All interop is source-generated <c>[LibraryImport]</c> (Native-AOT safe): the
/// <c>SHELLEXECUTEINFOW</c> struct is kept fully blittable (string fields are
/// hand-marshalled <see cref="IntPtr"/>s) so the generator needs no runtime
/// marshaller. No reflection, no COM. The relaunch is a no-op off Windows.
/// </remarks>
public static partial class Elevation
{
    /// <summary>
    /// True when the current process token is elevated (running as administrator).
    /// Always false off Windows.
    /// </summary>
    public static bool IsProcessElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }
        return IsProcessElevatedWindows();
    }

    [SupportedOSPlatform("windows")]
    private static bool IsProcessElevatedWindows()
    {
        IntPtr token = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out token))
            {
                return false;
            }

            // TOKEN_ELEVATION is a single DWORD (non-zero => elevated).
            var size = (uint)sizeof(uint);
            if (!GetTokenInformation(token, TokenElevation, out uint elevation, size, out _))
            {
                return false;
            }
            return elevation != 0;
        }
        finally
        {
            if (token != IntPtr.Zero)
            {
                CloseHandle(token);
            }
        }
    }

    /// <summary>
    /// Relaunch the current executable elevated (<c>runas</c>), forwarding
    /// <paramref name="args"/> verbatim, and block until the elevated child exits.
    /// Returns the child's process exit code so the caller can propagate it. If the
    /// user declines the UAC prompt (or the shell refuses), returns
    /// <paramref name="cancelledExitCode"/> (default <c>2</c>, "user cancelled").
    /// </summary>
    /// <remarks>
    /// The relaunch uses the running exe's own image path
    /// (<see cref="Environment.ProcessPath"/>). Off Windows, or when the process
    /// path is unavailable, this is a no-op returning
    /// <paramref name="cancelledExitCode"/>.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    public static int RelaunchElevatedAndWait(
        System.Collections.Generic.IReadOnlyList<string> args, int cancelledExitCode = 2) =>
        RelaunchElevatedAndWait(args, out _, cancelledExitCode);

    /// <summary>
    /// <see cref="RelaunchElevatedAndWait(System.Collections.Generic.IReadOnlyList{string}, int)"/>,
    /// additionally reporting whether an elevated child may have outlived this call.
    /// </summary>
    /// <param name="args">The arguments to forward to the elevated child.</param>
    /// <param name="childMayStillBeRunning">
    /// <c>false</c> only when this call is <em>certain</em> no elevated child can still
    /// be running: either none was created (the process path was unavailable, the shell
    /// refused, or the user declined the UAC prompt), or one was created and
    /// <c>WaitForSingleObject</c> observed it exit. <c>true</c> for the two cases in
    /// between — <c>ShellExecuteExW</c> reported success but handed back no process
    /// handle to wait on, or the wait itself failed. Both return
    /// <paramref name="cancelledExitCode"/>, so the return value alone cannot tell them
    /// apart from a genuine refusal.
    /// </param>
    /// <param name="cancelledExitCode">Exit code reported when the relaunch did not run.</param>
    /// <remarks>
    /// A caller that cleans up a resource the elevated child is meant to consume —
    /// the secret handoff envelope — MUST skip that cleanup while this is <c>true</c>,
    /// or it races a child that is still starting up and fails the install it was
    /// enabling. See <see cref="ElevationSecretHandoff.CleanUp"/>. (R18)
    /// </remarks>
    [SupportedOSPlatform("windows")]
    public static int RelaunchElevatedAndWait(
        System.Collections.Generic.IReadOnlyList<string> args,
        out bool childMayStillBeRunning,
        int cancelledExitCode = 2)
    {
        ArgumentNullException.ThrowIfNull(args);

        childMayStillBeRunning = false;

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            return cancelledExitCode;
        }

        var parameters = BuildCommandLine(args);

        IntPtr pVerb = Marshal.StringToHGlobalUni("runas");
        IntPtr pFile = Marshal.StringToHGlobalUni(exe);
        IntPtr pParams = Marshal.StringToHGlobalUni(parameters);
        try
        {
            var info = new SHELLEXECUTEINFOW
            {
                // Unsafe.SizeOf (not Marshal.SizeOf) — the struct is blittable, so
                // managed and native sizes are equal, and this stays AOT-clean.
                cbSize = (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<SHELLEXECUTEINFOW>(),
                fMask = SEE_MASK_NOCLOSEPROCESS,
                lpVerb = pVerb,
                lpFile = pFile,
                lpParameters = pParams,
                nShow = SW_SHOWNORMAL,
            };

            if (!ShellExecuteExW(ref info))
            {
                // ERROR_CANCELLED (1223) => user declined the UAC prompt. Any other
                // failure also surfaces as "cancelled": we could not run elevated,
                // so the machine install did not proceed. No process was created.
                return cancelledExitCode;
            }

            if (info.hProcess == IntPtr.Zero)
            {
                // SEE_MASK_NOCLOSEPROCESS was requested, so a successful call should
                // have handed back a handle. It did not, so there is nothing to wait
                // on — yet the shell reported success, which means a child MAY have
                // been created and may still be starting. Surfacing that is the whole
                // point of the out parameter: the caller must not tidy up behind a
                // process it cannot see.
                childMayStillBeRunning = true;
                return cancelledExitCode;
            }

            try
            {
                // A failed wait leaves the child's state unknown — it was created and
                // this call cannot vouch that it has exited.
                childMayStillBeRunning = WaitForSingleObject(info.hProcess, INFINITE) != WAIT_OBJECT_0;
                return GetExitCodeProcess(info.hProcess, out uint code) ? (int)code : cancelledExitCode;
            }
            finally
            {
                CloseHandle(info.hProcess);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pVerb);
            Marshal.FreeHGlobal(pFile);
            Marshal.FreeHGlobal(pParams);
        }
    }

    /// <summary>
    /// Quote and join <paramref name="args"/> into a single Win32 command-line
    /// string suitable for <c>ShellExecuteExW</c>'s <c>lpParameters</c>. Each
    /// argument is quoted per the standard CommandLineToArgvW rules so paths with
    /// spaces and embedded quotes round-trip through the elevated relaunch.
    /// </summary>
    internal static string BuildCommandLine(System.Collections.Generic.IReadOnlyList<string> args)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < args.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(' ');
            }
            AppendQuoted(sb, args[i]);
        }
        return sb.ToString();
    }

    private static void AppendQuoted(StringBuilder sb, string arg)
    {
        // Only quote when needed (whitespace or a quote present); mirrors the
        // MSVC / CommandLineToArgvW quoting algorithm for backslashes-before-quote.
        if (arg.Length > 0 && arg.IndexOfAny(QuoteTriggers) < 0)
        {
            sb.Append(arg);
            return;
        }

        sb.Append('"');
        var backslashes = 0;
        foreach (var c in arg)
        {
            switch (c)
            {
                case '\\':
                    backslashes++;
                    break;
                case '"':
                    sb.Append('\\', backslashes * 2 + 1);
                    backslashes = 0;
                    sb.Append('"');
                    break;
                default:
                    if (backslashes > 0)
                    {
                        sb.Append('\\', backslashes);
                        backslashes = 0;
                    }
                    sb.Append(c);
                    break;
            }
        }
        // Escape trailing backslashes so they don't escape the closing quote.
        sb.Append('\\', backslashes * 2);
        sb.Append('"');
    }

    private static readonly char[] QuoteTriggers = { ' ', '\t', '\n', '\v', '"' };

    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenElevation = 20; // TOKEN_INFORMATION_CLASS.TokenElevation
    private const uint SEE_MASK_NOCLOSEPROCESS = 0x00000040;
    private const int SW_SHOWNORMAL = 1;
    private const uint INFINITE = 0xFFFFFFFF;
    private const uint WAIT_OBJECT_0 = 0x00000000;

    // Fully blittable: the string fields are hand-marshalled IntPtrs so the
    // LibraryImport source generator can pass this by ref with no runtime
    // marshaller (Native-AOT clean).
    [StructLayout(LayoutKind.Sequential)]
    private struct SHELLEXECUTEINFOW
    {
        public uint cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        public IntPtr lpFile;
        public IntPtr lpParameters;
        public IntPtr lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        public IntPtr lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    [SupportedOSPlatform("windows")]
    [LibraryImport("shell32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShellExecuteExW(ref SHELLEXECUTEINFOW lpExecInfo);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(
        IntPtr tokenHandle, int tokenInformationClass, out uint tokenInformation, uint tokenInformationLength, out uint returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(IntPtr process, out uint exitCode);
}
