using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Threading.Tasks;
using SigilBuild.Core.Configuration;
using SigilBuild.Core.Diagnostics;

namespace SigilBuild.Cli.Commands;

public static class ValidateCommand
{
    public static Command Build()
    {
        var pathArg = new Argument<string>("path", () => "sigil.yaml", "Path to the sigil.yaml manifest");
        var cmd = new Command("validate", "Validate a sigil.yaml manifest");
        cmd.AddArgument(pathArg);
        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var path = ctx.ParseResult.GetValueForArgument(pathArg);
            var result = await ManifestLoader.LoadAsync(path, new ProcessEnvironmentReader());
            DiagnosticReporter.Write(Console.Error, result.Diagnostics, useColor: false);
            if (result.Manifest is null)
            {
                ctx.ExitCode = 1;
            }
            else
            {
                Console.Out.WriteLine($"OK: {path}");
                ctx.ExitCode = 0;
            }
        });
        return cmd;
    }
}
