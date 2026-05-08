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
            var blob = WrapperBlob.LoadFromSelf(); // stubbed in Task 12; real impl Task 14
            var parsed = CommandLineParser.Parse(args, blob.Parameters);
            var ctx = StepContext.From(blob, parsed);

            // Phase routing per mode:
            //   Install  → pre_install + install_steps + post_install (Task 18).
            //   Update   → update_steps only; the manifest does not currently
            //              model update-time pre/post hooks.
            //   Uninstall → wired in Task 19 (auto-derived uninstall steps).
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
                case WrapperMode.Uninstall:
                    // TODO(Task 19): route to auto-derived uninstall steps.
                    preInstall = Array.Empty<InstallStep>();
                    mainSteps = Array.Empty<InstallStep>();
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
            return result.Success ? 0 : 1;
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine($"usage error: {ex.Message}");
            return 64; // EX_USAGE per sysexits.h convention
        }
    }
}
