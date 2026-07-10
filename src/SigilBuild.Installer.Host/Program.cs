using System;
using Avalonia;

namespace SigilBuild.Installer.Host;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // WinExe has no console — install a hard backstop so any unhandled
        // exception during Avalonia init lands in the wizard log instead of
        // disappearing into the void. The wrapper's stdout/stderr drain
        // doesn't help once Avalonia detaches the standard handles.
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
}
