using System;
using Avalonia;

namespace SigilBuild.Installer.Host;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var builder = BuildAvaloniaApp();
        // StartWithClassicDesktopLifetime returns 0 on normal exit.
        // We override with the VM's outcome code so the OS receives 1602 on user cancel.
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
}
