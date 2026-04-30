namespace SigilBuild.Cli;

public static class Program
{
    private const string Version = "0.0.1-alpha";

    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            System.Console.Error.WriteLine("usage: sigil <command> [options]");
            System.Console.Error.WriteLine("Try 'sigil --version' to verify the install.");
            return 64; // EX_USAGE
        }

        var first = args[0];
        if (first is "--version" or "-v" or "version")
        {
            System.Console.WriteLine(Version);
            return 0;
        }

        System.Console.Error.WriteLine($"sigil: unknown command '{first}'");
        return 64;
    }
}
