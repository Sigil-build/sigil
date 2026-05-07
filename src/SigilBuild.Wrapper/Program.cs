using System;

namespace SigilBuild.Wrapper;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--version")
        {
            Console.WriteLine("SigilBuild.Wrapper runtime (placeholder)");
            return 0;
        }
        Console.Error.WriteLine("not yet implemented");
        return 2;
    }
}
