using System;
using System.IO;
using System.Linq;

namespace SigilBuild.Packaging.Msix;

public static class WindowsSdkLocator
{
    private static readonly string[] DefaultRoots =
    [
        @"C:\Program Files (x86)\Windows Kits\10\bin",
        @"C:\Program Files\Windows Kits\10\bin",
    ];

    public static bool TryLocateBin(out string binDir)
    {
        foreach (var root in DefaultRoots)
        {
            if (TryLocateBinFromRoot(root, out binDir)) return true;
        }
        binDir = string.Empty;
        return false;
    }

    public static bool TryLocateBinFromRoot(string root, out string binDir)
    {
        binDir = string.Empty;
        if (!Directory.Exists(root)) return false;

        var versioned = Directory.GetDirectories(root)
            .Where(d => Version.TryParse(Path.GetFileName(d), out _))
            .OrderByDescending(d => Version.Parse(Path.GetFileName(d)));

        foreach (var v in versioned)
        {
            var x64 = Path.Combine(v, "x64");
            if (File.Exists(Path.Combine(x64, "MakeAppx.exe")))
            {
                binDir = x64;
                return true;
            }
        }
        return false;
    }
}
