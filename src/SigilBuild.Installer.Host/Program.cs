using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using SigilBuild.Wrapper.Cli;
using SigilBuild.Wrapper.Core.Localization;
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

        if (args.Length == 1 && (args[0] == "/?" || args[0].Equals("/help", StringComparison.OrdinalIgnoreCase)))
        {
            AttachParentConsole();
            Console.WriteLine(HelpText.Render());
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

        // P9 (gap G10): resolve this session's chrome language now — installer.language
        // (fixed) -> /lang -> the OS UI-language preference list -> en — and set
        // SessionLanguage.Current. MUST run before ANY UI is constructed, including
        // the elevated relaunch below (harmless: the elevated child re-resolves
        // identically from the same blob + argv) and, further down, the pre-Avalonia
        // single-instance MessageBoxW, itself a catalog string. The resolver depends
        // only on the blob and Win32, never on Avalonia, so this ordering works.
        session.ResolveSessionLanguage();
        if (session.LanguageConflictNote is not null)
        {
            // Design §2.1: the manifest pin wins; the flag is ignored, not fatal
            // (exit code stays 0). Also flushed into the /LOG sink (if requested)
            // the first time it opens; this additionally records it in the
            // wizard's always-on diagnostic log.
            InstallerLog.Info(session.LanguageConflictNote);
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

        // P6 (gap G17): single-instance guard. Taken AFTER the elevation branch — the
        // un-elevated parent above never installs, so it must not hold the mutex while
        // the elevated child (which does) tries to take it. Held for the whole run;
        // the OS releases it if the process dies, so a crash never wedges the name.
        using var instanceLock = SetupInstanceLock.TryAcquire(session.AppId, session.ResolvedScope);
        if (instanceLock is null)
        {
            if (session.Silent)
            {
                AttachParentConsole();
                Console.Error.WriteLine(
                    "another setup for this application is already running — close it and try again.");
            }
            else if (OperatingSystem.IsWindows())
            {
                // A WinExe has no console, so the headed path needs a real notice.
                // P9: catalog-driven — already_running.body / already_running.caption,
                // resolved through the session language set above (before ANY UI,
                // including this pre-Avalonia MessageBox).
                _ = MessageBoxW(
                    IntPtr.Zero,
                    Strings.AlreadyRunningBody(SessionLanguage.Current),
                    Strings.AlreadyRunningCaption(SessionLanguage.Current),
                    MB_OK | MB_ICONINFORMATION);
            }
            return InstallSession.AlreadyRunningExitCode;
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

        // WinExe has no console — install a hard backstop so any unhandled
        // exception during Avalonia init lands in the wizard log instead of
        // disappearing into the void. The wrapper's stdout/stderr drain
        // doesn't help once Avalonia detaches the standard handles. (Ported from PR #8.)
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                InstallerLog.Error("AppDomain.UnhandledException", ex);
            else
                InstallerLog.Error($"AppDomain.UnhandledException (non-Exception): {e.ExceptionObject}");
        };

        InstallerLog.Info($"wizard started: pid={Environment.ProcessId}, argv=[{string.Join(' ', args)}], cwd={Environment.CurrentDirectory}");

        try
        {
            InstallerLog.Info("BuildAvaloniaApp() → StartWithClassicDesktopLifetime()");
            var builder = BuildAvaloniaApp();
            var avaloniaExitCode = builder.StartWithClassicDesktopLifetime(args);
            InstallerLog.Info($"StartWithClassicDesktopLifetime returned {avaloniaExitCode}");

            if (avaloniaExitCode != 0)
                return avaloniaExitCode;

            if (builder.Instance is App app)
            {
                InstallerLog.Info($"wizard exiting with OutcomeExitCode={app.OutcomeExitCode}");
                return app.OutcomeExitCode;
            }

            InstallerLog.Info("builder.Instance is not App — returning 0");
            return 0;
        }
        catch (Exception ex)
        {
            InstallerLog.Error("wizard Main threw", ex);
            throw;
        }
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
    private const uint MB_OK = 0x0;
    private const uint MB_ICONINFORMATION = 0x40;

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(uint dwProcessId);

    // P6 (gap G17): the friendly "already running" notice for the headed path — a
    // plain MessageBox, since this fires before Avalonia is initialised.
    [SupportedOSPlatform("windows")]
    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(IntPtr hWnd, string lpText, string lpCaption, uint uType);
}
