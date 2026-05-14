using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SigilBuild.Core.Manifest;
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

        // Echo the log path before any work — if the wrapper crashes on the
        // first line of WrapperBlob.LoadFromSelf the user still has a path to
        // look at (a freshly-initialised log with the startup banner).
        WrapperLog.Info($"wrapper started, pid={System.Environment.ProcessId}, argv=[{string.Join(' ', args)}]");
        WrapperLog.Info($"elevation: IsElevated={IsElevated()}, user={System.Environment.UserName}");
        if (WrapperLog.LogPath is { } logPath)
        {
            Console.Error.WriteLine($"sigil-wrapper: log → {logPath}");
        }

        try
        {
            var blob = WrapperBlob.LoadFromSelf();
            WrapperLog.Info($"blob loaded: appId={blob.AppId}, params={blob.Parameters.Count}, pre={blob.PreInstall.Count}, install={blob.InstallSteps.Count}, post={blob.PostInstall.Count}, update={blob.UpdateSteps.Count}");

            var parsed = CommandLineParser.Parse(args, blob.Parameters);
            WrapperLog.Info($"argv parsed: mode={parsed.Mode}, silent={parsed.Silent}, params={parsed.Values.Count}");

            // Uninstall is a separate engine — it loads the persisted
            // RollbackJournal and replays it instead of running the
            // pre/install/post pipeline.
            if (parsed.Mode == WrapperMode.Uninstall)
            {
                WrapperLog.Info("dispatching to uninstall engine");
                return await RunUninstallAsync(blob).ConfigureAwait(false);
            }

            // Interactive install path (no /S flag) — show the branded wizard
            // when its host bundle is embedded. The wizard's Installing screen
            // re-launches this same setup.exe with /S as a child process; that
            // child runs install_steps under inherited UAC. The wizard waits
            // for the child to exit, then transitions to Finish.
            //
            // Contract:
            //   wizardExit == null → no wizard bundled, fall through to
            //                        headless install_steps in THIS process.
            //   wizardExit == 0    → wizard's child already ran install_steps;
            //                        return 0 without re-running.
            //   wizardExit != 0    → user cancelled / wizard failed; surface
            //                        the MSI-convention exit code (1602/1603/…).
            if (parsed.Mode == WrapperMode.Install && !parsed.Silent)
            {
                WrapperLog.Info("interactive install — attempting to launch wizard");
                var wizardExit = await RunWizardIfBundledAsync().ConfigureAwait(false);
                if (wizardExit is null)
                {
                    WrapperLog.Info("no installer-host bundle embedded — falling through to headless install_steps");
                }
                else
                {
                    WrapperLog.Info($"wizard exited with code {wizardExit.Value}");
                    if (wizardExit.Value != 0)
                    {
                        WrapperLog.Info("wizard returned non-zero — skipping install_steps");
                        return wizardExit.Value;
                    }
                    // wizard exit 0 means its Installing screen already ran a
                    // setup.exe /S child to completion. Don't run install_steps
                    // again — that would double-install (and the auto-derived
                    // uninstall journal would be corrupted).
                    WrapperLog.Info("wizard returned 0 — install completed by its subprocess; skipping engine");
                    return 0;
                }
            }
            else
            {
                WrapperLog.Info($"skipping wizard (mode={parsed.Mode}, silent={parsed.Silent})");
            }

            var ctx = StepContext.From(blob, parsed);

            // Phase routing per mode:
            //   Install → pre_install + install_steps + post_install (Task 18).
            //   Update  → update_steps only; the manifest does not currently
            //             model update-time pre/post hooks.
            IReadOnlyList<InstallStep> preInstall;
            IEnumerable<InstallStep> mainSteps;
            IReadOnlyList<InstallStep> postInstall;
            switch (parsed.Mode)
            {
                case WrapperMode.Install:
                    preInstall = blob.PreInstall;
                    mainSteps = blob.InstallSteps;
                    postInstall = blob.PostInstall;
                    break;
                case WrapperMode.Update:
                    preInstall = Array.Empty<InstallStep>();
                    mainSteps = blob.UpdateSteps;
                    postInstall = Array.Empty<InstallStep>();
                    break;
                default:
                    preInstall = Array.Empty<InstallStep>();
                    mainSteps = Array.Empty<InstallStep>();
                    postInstall = Array.Empty<InstallStep>();
                    break;
            }

            // Extract the embedded SIGIL_PAYLOAD_V1 zip to a temp dir and set
            // it as cwd before running install_steps. file_copy steps with
            // relative `from:` paths resolve against the extracted payload —
            // without this, the engine would try to read files from the
            // wrapper's *original* cwd (typically the dist directory next to
            // setup.exe, which contains only setup.exe itself) and silently
            // copy nothing, leaving sc.exe's binPath pointing into an empty
            // dir → StartService FAILED 2 / ERROR_FILE_NOT_FOUND.
            using var payload = PayloadExtractor.Prepare();

            WrapperLog.Info($"running install engine: pre={preInstall.Count}, main=<enumerated>, post={postInstall.Count}");
            var result = await new InstallEngine().RunAsync(
                preInstall: preInstall,
                installSteps: mainSteps,
                postInstall: postInstall,
                ctx: ctx).ConfigureAwait(false);

            WrapperLog.Info($"install engine complete: success={result.Success}, journal-records={result.Journal.Records.Count}{(result.Error is null ? "" : ", error=" + result.Error)}");

            if (!result.Success)
            {
                if (result.Error is not null)
                {
                    Console.Error.WriteLine(result.Error);
                }
                return 1;
            }

            // On a successful install, persist the journal and write the ARP
            // entry so the user can find the program in "Add or Remove
            // Programs". The DisplayName/Version/Publisher placeholders are
            // an acknowledged Task 19 gap — Task 20+ will thread the manifest
            // App.* fields through WrapperBlob and into this call site.
            if (parsed.Mode == WrapperMode.Install && OperatingSystem.IsWindows())
            {
                UninstallStateStore.Save(blob.AppId, result.Journal);
                ArpRegistration.Register(new ArpRegistration.Entry(
                    AppId: blob.AppId,
                    DisplayName: blob.AppId,
                    DisplayVersion: "1.0.0",
                    Publisher: "Unknown",
                    UninstallString: ArpRegistration.BuildUninstallString(
                        Environment.ProcessPath ?? "."),
                    EstimatedSizeBytes: 0));
            }
            return 0;
        }
        catch (UsageException ex)
        {
            WrapperLog.Error($"usage error: {ex.Message}");
            Console.Error.WriteLine($"usage error: {ex.Message}");
            return 64; // EX_USAGE per sysexits.h convention
        }
        catch (Exception ex)
        {
            // Catch-all so unhandled exceptions hit the log file instead of
            // disappearing into a console window the user double-clicked
            // (and which closes when the process exits). We still rethrow so
            // the OS sees a non-zero exit code via the unhandled-exception
            // path; this just buys us a forensic trail.
            WrapperLog.Error("unhandled exception", ex);
            throw;
        }
    }

    /// <summary>
    /// Best-effort detection of whether the current process is running with
    /// administrator privileges. Used purely for logging — actual elevation
    /// requirements are enforced via the manifest's
    /// <c>requestedExecutionLevel="requireAdministrator"</c>.
    /// </summary>
    private static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Extract + launch the bundled installer host (Avalonia wizard) when the
    /// <c>SIGIL_INSTALLER_HOST_V1</c> resource is embedded. Returns the
    /// wizard's exit code (0 = proceed, 1602 = user cancel, 1603 = failure),
    /// or <c>null</c> when no wizard is bundled — in which case the caller
    /// falls through to the headless install_steps path.
    /// </summary>
    private static async Task<int?> RunWizardIfBundledAsync()
    {
        using var launcher = InstallerHostLauncher.TryPrepare();
        if (launcher is null)
        {
            return null; // no wizard bundled — headless install
        }
        return await launcher.RunAsync(System.Threading.CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Drive the auto-derived uninstall flow:
    /// <list type="number">
    ///   <item>Run any <c>uninstall</c> steps declared in the manifest —
    ///         services, scheduled tasks, custom scripts the install journal
    ///         doesn't cover get torn down here, BEFORE the journal replays.</item>
    ///   <item>Load <c>%ProgramData%\Sigil\&lt;AppId&gt;\uninstall.json</c>,
    ///         replay the rollback journal in reverse (file copies, registry
    ///         writes, env vars, shortcuts).</item>
    ///   <item>Remove the ARP entry and clean up state.</item>
    /// </list>
    /// </summary>
    private static async Task<int> RunUninstallAsync(WrapperBlob blob)
    {
        // uninstall runs in the SAME context as install_steps would, so
        // ${parameters.*} and ${app.*} resolve identically. Failures by
        // default abort the uninstall; on_failure: continue marks a step as
        // best-effort (typical for "stop service that may not exist" patterns).
        if (blob.Uninstall.Count > 0)
        {
            WrapperLog.Info($"uninstall: running {blob.Uninstall.Count} step(s) before journal replay");
            var ctx = StepContext.From(blob, new ParsedCommandLine { Mode = WrapperMode.Uninstall });
            var preResult = await new InstallEngine().RunAsync(
                preInstall: Array.Empty<InstallStep>(),
                installSteps: blob.Uninstall,
                postInstall: Array.Empty<InstallStep>(),
                ctx: ctx).ConfigureAwait(false);
            if (!preResult.Success)
            {
                WrapperLog.Error($"uninstall failed: {preResult.Error}");
                if (preResult.Error is not null)
                {
                    Console.Error.WriteLine(preResult.Error);
                }
                // Continue with journal replay anyway — the user wants uninstall
                // to make progress; uninstall failure is logged but not fatal.
            }
            else
            {
                WrapperLog.Info("uninstall complete");
            }
        }

        var result = await new UninstallEngine().RunAsync(blob.AppId).ConfigureAwait(false);
        if (!result.Success)
        {
            if (result.Error is not null)
            {
                Console.Error.WriteLine(result.Error);
            }
            return 1;
        }
        return 0;
    }
}
