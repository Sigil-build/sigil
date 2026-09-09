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

            // P9 (gap G10): resolve this session's chrome language now — installer.language
            // (fixed) -> /lang -> the OS UI-language preference list -> en. MUST
            // run before any output is produced, mirroring the host's ordering
            // exactly so both entry points resolve identically. Any conflict note
            // is flushed into the /LOG sink (if requested) the first time it
            // opens — this console entry point has no separate diagnostic log to
            // additionally write it to.
            session.ResolveSessionLanguage();

            // T12 — self-elevation. A resolved per-machine scope from a
            // non-elevated process relaunches self with the `runas` verb,
            // forwarding all args, and propagates the elevated child's exit code.
            // Per-user installs stay prompt-free. Mirrors the host entry path.
            if (OperatingSystem.IsWindows()
                && session.RequiresElevation
                && session.Mode != WrapperMode.Update)
            {
                // R18: relaunch with the handoff-rewritten vector, never raw argv —
                // a /P<secret>=<value> token would otherwise be published to every
                // process-creation auditor on the box. The finally is the parent's
                // best-effort cleanup for a declined UAC prompt, or a child that died
                // before consuming the envelope; normally the child has already
                // deleted it as it read it. It is SKIPPED whenever a child may still
                // be starting up (see the out parameter) — deleting the envelope from
                // under it would fail the very install the handoff enables. Seeded
                // `true` so a throw before the call assumes the unsafe case.
                var relaunchArgs = session.BuildElevationRelaunchArgs(args);
                var childMayStillBeRunning = true;
                try
                {
                    return Elevation.RelaunchElevatedAndWait(
                        relaunchArgs, out childMayStillBeRunning);
                }
                finally
                {
                    ElevationSecretHandoff.CleanUp(relaunchArgs, childMayStillBeRunning);
                }
            }

            // P6 (gap G17): single-instance guard. Taken AFTER the elevation branch —
            // the un-elevated parent above never installs, so it must not hold the
            // mutex while the elevated child (which does) tries to take it.
            //
            // R76: the mode is passed because ONE process legitimately runs while the
            // guard is already held — the prior version's uninstall.exe that an upgrade
            // in this very app+scope spawned for its teardown. It is admitted only
            // against a handoff its parent minted (SetupInstanceLock.HandoffAdmits);
            // an ordinary second Setup.exe is refused exactly as before.
            using var instanceLock = SetupInstanceLock.TryAcquire(
                session.AppId, session.ResolvedScope, session.Mode, out var lockRefusal);
            if (instanceLock is null)
            {
                // R34: two different situations reach here. Say which — an operator
                // chasing "already running" with nothing running needs to know the name
                // was occupied rather than held.
                Console.Error.WriteLine(
                    lockRefusal == SetupInstanceLock.SetupLockRefusal.NameNotAvailable
                        ? "the single-instance guard for this application could not be taken: its " +
                          "name is already occupied by another object. Another setup may be " +
                          "running, or the name has been squatted. Nothing was installed."
                        : "another setup for this application is already running — close it and try again.");
                return InstallSession.AlreadyRunningExitCode;
            }

            if (lockRefusal == SetupInstanceLock.SetupLockRefusal.GuardUnavailable)
            {
                // R34: proceeding WITHOUT the guard, out loud. This branch used to return
                // a sentinel indistinguishable from a real lock and say nothing.
                Console.Error.WriteLine(
                    "note: the single-instance guard could not be created for this run — " +
                    "a concurrent setup of the same application would not be detected.");
            }

            // R76: hand the lock to the session so a P3 upgrade teardown can pass it on
            // to the prior version's uninstaller. Only an OWNING lock mints a handoff,
            // so setting it unconditionally is safe (an admitted or sentinel lock mints
            // nothing).
            session.InstanceLock = instanceLock;

            return await session.RunHeadlessAsync(Console.Out, Console.Error).ConfigureAwait(false);
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine($"usage error: {ex.Message}");
            return 64; // EX_USAGE per sysexits.h convention
        }
    }
}
