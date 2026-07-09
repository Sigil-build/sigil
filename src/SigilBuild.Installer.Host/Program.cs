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

        // Headless whenever /silent or /verysilent is present, or a
        // non-interactive mode (uninstall / the unsupported update) is requested.
        // Interactive uninstall is Task T15; for now uninstall is headless-only.
        if (session.Silent || session.Mode != WrapperMode.Install)
        {
            AttachParentConsole();
            return session.RunHeadlessAsync(Console.Out, Console.Error).GetAwaiter().GetResult();
        }

        // Interactive install: launch the branded Avalonia wizard, which drives
        // the same InstallSession engine on its Installing screen.
        HostRuntime.Session = session;

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
