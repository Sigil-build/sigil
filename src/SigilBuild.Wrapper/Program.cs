using System;
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
            var steps = parsed.Mode switch
            {
                WrapperMode.Install => blob.InstallSteps,
                WrapperMode.Update => blob.UpdateSteps,
                WrapperMode.Uninstall => Array.Empty<InstallStep>(), // Task 19 wires this
                _ => Array.Empty<InstallStep>(),
            };

            var result = await new InstallEngine().RunAsync(steps, ctx).ConfigureAwait(false);
            return result.Success ? 0 : 1;
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine($"usage error: {ex.Message}");
            return 64; // EX_USAGE per sysexits.h convention
        }
    }
}
