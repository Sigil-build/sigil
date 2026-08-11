using System;
using System.IO;
using Xunit;

namespace SigilBuild.Packaging.Tests.ExeWrapper;

/// <summary>
/// Reports a genuine Skipped result when the kiosk sample's separately-built
/// <c>Setup.exe</c> is absent, instead of returning early and reporting as Passed
/// (register row R6). Unlike <see cref="RuntimeStagedFactAttribute"/> (the staged
/// AOT installer-host runtime, which every packaging test needs), this precondition
/// is specific to <c>tests/kiosk/</c>: a separate, out-of-band sample build that
/// produces <c>tests/kiosk/dist/Embed.Infinity.Kiosk-1.0.0-x64-Setup.exe</c> and is
/// not part of the normal repo checkout or `dotnet build`/`dotnet test` flow, so it
/// genuinely varies per machine rather than indicating a broken build.
/// </summary>
internal sealed class KioskSetupFactAttribute : FactAttribute
{
    public KioskSetupFactAttribute()
    {
        if (!File.Exists(SetupPath))
        {
            Skip = "Kiosk sample test: " + SetupPath + " not found (build the tests/kiosk sample first)";
        }
    }

    private static string SetupPath => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..",
        "tests", "kiosk", "dist", "Embed.Infinity.Kiosk-1.0.0-x64-Setup.exe"));
}
