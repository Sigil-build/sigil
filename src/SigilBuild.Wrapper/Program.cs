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

        try
        {
            var blob = WrapperBlob.LoadFromSelf();
            var parsed = CommandLineParser.Parse(args, blob.Parameters);

            // Uninstall is a separate engine — it loads the persisted
            // RollbackJournal and replays it instead of running the
            // pre/install/post pipeline.
            if (parsed.Mode == WrapperMode.Uninstall)
            {
                return await RunUninstallAsync(blob).ConfigureAwait(false);
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

            var result = await new InstallEngine().RunAsync(
                preInstall: preInstall,
                installSteps: mainSteps,
                postInstall: postInstall,
                ctx: ctx).ConfigureAwait(false);

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
            Console.Error.WriteLine($"usage error: {ex.Message}");
            return 64; // EX_USAGE per sysexits.h convention
        }
    }

    /// <summary>
    /// Drive the auto-derived uninstall flow: load
    /// <c>%ProgramData%\Sigil\&lt;AppId&gt;\uninstall.json</c>, replay the
    /// journal in reverse, remove the ARP entry, and clean up state.
    /// </summary>
    private static async Task<int> RunUninstallAsync(WrapperBlob blob)
    {
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
