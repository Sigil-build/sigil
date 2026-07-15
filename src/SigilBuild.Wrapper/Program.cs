using System;
using System.Threading.Tasks;
using SigilBuild.Wrapper.Cli;
using SigilBuild.Wrapper.Engine;

namespace SigilBuild.Wrapper;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--version")
        {
            Console.WriteLine("SigilBuild.Wrapper runtime (placeholder)");
            return 0;
        }

        if (args.Length == 1 && (args[0] == "/?" || args[0].Equals("/help", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine(HelpText.Render());
            return 0;
        }

        try
        {
            // The console shell is always headless; the Avalonia host shares the
            // same InstallSession for its /silent path and its GUI wizard.
            var session = InstallSession.Create(args);

            // T12 — self-elevation. A resolved per-machine scope from a
            // non-elevated process relaunches self with the `runas` verb,
            // forwarding all args, and propagates the elevated child's exit code.
            // Per-user installs stay prompt-free. Mirrors the host entry path.
            if (OperatingSystem.IsWindows()
                && session.RequiresElevation
                && session.Mode != WrapperMode.Update)
            {
                return Elevation.RelaunchElevatedAndWait(args);
            }

            // P6 (gap G17): single-instance guard. Taken AFTER the elevation branch —
            // the un-elevated parent above never installs, so it must not hold the
            // mutex while the elevated child (which does) tries to take it.
            using var instanceLock = SetupInstanceLock.TryAcquire(session.AppId, session.ResolvedScope);
            if (instanceLock is null)
            {
                Console.Error.WriteLine(
                    "another setup for this application is already running — close it and try again.");
                return InstallSession.AlreadyRunningExitCode;
            }

            return await session.RunHeadlessAsync(Console.Out, Console.Error).ConfigureAwait(false);
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine($"usage error: {ex.Message}");
            return 64; // EX_USAGE per sysexits.h convention
        }
    }
}
