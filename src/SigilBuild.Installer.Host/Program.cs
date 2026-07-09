using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using SigilBuild.Wrapper.Cli;
using SigilBuild.Wrapper.Engine;

namespace SigilBuild.Installer.Host;

public static partial class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--version")
        {
            Console.WriteLine("SigilBuild.Installer.Host runtime (placeholder)");
            return 0;
        }

        // The one CommandLineParser + WrapperBlob load, shared with the console
        // wrapper. A usage/validation error (bad flag, undeclared parameter, or a
        // required parameter missing in silent mode) exits 64.
        InstallSession session;
        try
        {
            session = InstallSession.Create(args);
        }
        catch (UsageException ex)
        {
            AttachParentConsole();
            Console.Error.WriteLine($"usage error: {ex.Message}");
            return 64;
        }

        // T12 — self-elevation. This MUST run before any scope-requiring work
        // (payload extraction, HKLM/Program Files writes) and before the T18 GUI
        // native bootstrap below. The host manifest requests `asInvoker`, so a
        // per-user install never triggers UAC; only a resolved per-machine scope
        // from a non-elevated process relaunches self with the `runas` verb,
        // forwarding ALL original args, and propagates the elevated child's exit
        // code as this process's exit code. Update mode is unsupported and never
        // elevates — it falls through to the headless 64 path below.
        if (OperatingSystem.IsWindows()
            && session.RequiresElevation
            && session.Mode != WrapperMode.Update)
        {
            AttachParentConsole();
            return Elevation.RelaunchElevatedAndWait(args);
        }

        // Headless whenever /silent or /verysilent is present (this includes the
        // ARP UninstallString's `/S /Uninstall`), or the unsupported /Update mode is
        // requested. An interactive uninstall (uninstall.exe double-clicked, no /S,
        // T15) is NOT silent, so it falls through to the branded GUI below.
        if (session.Silent || session.Mode == WrapperMode.Update)
        {
            AttachParentConsole();
            return session.RunHeadlessAsync(Console.Out, Console.Error).GetAwaiter().GetResult();
        }

        // Interactive GUI: the install wizard, or the T15 uninstall confirm flow —
        // App picks the window from session.Mode. Both drive the same InstallSession
        // engine.
        HostRuntime.Session = session;

        // T18: make a standalone stamped Setup.exe self-contained. Native AOT
        // publishes the host's Skia/ANGLE/HarfBuzz native DLLs BESIDE the exe; a
        // stamped Setup.exe instead carries them inside its SIGIL_RUNTIME_V1
        // resource. Extract them to a per-user cache and add that directory to the
        // native DLL search path BEFORE the Avalonia AppBuilder is created, so the
        // first Skia/Avalonia native load resolves. GUI path ONLY — the headless
        // /silent, /verysilent and /S-uninstall paths returned above and never touch
        // Skia, so they skip this (the interactive uninstall window, which does reach
        // here, needs it). On an un-stamped dev run (no resource) this is a
        // no-op: the native DLLs already sit beside the exe. Idempotent across
        // re-runs (content-keyed cache dir + completion marker).
        if (OperatingSystem.IsWindows())
        {
            NativeRuntimeBootstrap.EnsureNativeDependenciesLoadable();
        }

        var builder = BuildAvaloniaApp();
        // StartWithClassicDesktopLifetime returns 0 on normal exit; we override
        // with the wizard's outcome so the OS receives 1 on step failure and 2
        // on user cancel.
        var avaloniaExitCode = builder.StartWithClassicDesktopLifetime(args);
        if (avaloniaExitCode != 0)
            return avaloniaExitCode;

        if (builder.Instance is App app)
            return app.OutcomeExitCode;

        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    /// <summary>
    /// Best-effort: a WinExe has no console of its own, so attach the launching
    /// shell's console for the <c>/silent</c> path. Failure is ignored — the
    /// exit code is the contract; the echoed log is a convenience.
    /// </summary>
    private static void AttachParentConsole()
    {
        if (OperatingSystem.IsWindows())
        {
            _ = AttachConsole(ATTACH_PARENT_PROCESS);
        }
    }

    private const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(uint dwProcessId);
}
