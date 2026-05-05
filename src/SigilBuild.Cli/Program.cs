using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using System.Threading.Tasks;
using SigilBuild.Cli.Commands;

namespace SigilBuild.Cli;

public static class Program
{
    public const string Version = "0.0.1-alpha";

    public static int Main(string[] args) => MainAsync(args).GetAwaiter().GetResult();

    public static async Task<int> MainAsync(string[] args)
    {
        var root = new RootCommand("sigil — declarative desktop-software distribution");
        root.AddCommand(ValidateCommand.Build());
        root.AddCommand(InitCommand.Build());

        // Custom --version handling so the existing exact-match tests still pass.
        if (args.Length > 0 && (args[0] == "--version" || args[0] == "-v" || args[0] == "version"))
        {
            System.Console.WriteLine(Version);
            return 0;
        }
        if (args.Length == 0)
        {
            System.Console.Error.WriteLine("usage: sigil <command> [options]");
            System.Console.Error.WriteLine("Try 'sigil --version' to verify the install.");
            return 64;
        }

        var parser = new CommandLineBuilder(root).UseDefaults().Build();
        return await parser.InvokeAsync(args);
    }
}
