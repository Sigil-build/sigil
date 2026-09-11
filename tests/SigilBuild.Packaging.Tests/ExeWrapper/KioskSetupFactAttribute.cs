using System;
using System.IO;
using Xunit;

namespace SigilBuild.Packaging.Tests.ExeWrapper;

/// <summary>
/// Reports a genuine Skipped result when the kiosk sample's separately-built
/// <c>Setup.exe</c> is absent, instead of returning early and reporting as Passed.
/// Unlike <see cref="RuntimeStagedFactAttribute"/> (the staged
/// AOT installer-host runtime, which every packaging test needs), this precondition
/// is specific to <c>tests/kiosk/</c>: a separate, out-of-band sample build that
/// produces <c>tests/kiosk/dist/Embed.Infinity.Kiosk-1.0.0-x64-Setup.exe</c> and is
/// not part of the normal repo checkout or `dotnet build`/`dotnet test` flow, so it
/// genuinely varies per machine rather than indicating a broken build. (R6)
/// </summary>
internal sealed class KioskSetupFactAttribute : FactAttribute
{
    public KioskSetupFactAttribute()
    {
        if (!File.Exists(SetupPath))
        {
            Skip = "Kiosk sample test: " + SetupPath +
                " not found (this is a separate, out-of-band sample build — " +
                "run the tests/kiosk packing steps to produce that Setup.exe first)";
        }
    }

    /// <summary>
    /// The kiosk sample's packed installer. Resolved by walking UP from the test
    /// assembly's output directory until a repo marker (<c>Sigil.slnx</c>) is found,
    /// then appending the sample's known repo-relative path — rather than a fixed
    /// count of <c>".."</c> segments, which silently drifts (and can walk one
    /// directory too far, landing outside the repository entirely) whenever the
    /// assembly's own output path depth changes (TFM bump, build configuration,
    /// etc.). (R6)
    /// </summary>
    internal static string SetupPath => Path.Combine(
        FindRepoRoot(AppContext.BaseDirectory),
        "tests", "kiosk", "dist", "Embed.Infinity.Kiosk-1.0.0-x64-Setup.exe");

    private static string FindRepoRoot(string startDirectory)
    {
        for (var dir = new DirectoryInfo(startDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Sigil.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException(
            $"could not locate the Sigil repo root (no Sigil.slnx found) walking up from '{startDirectory}'");
    }
}
