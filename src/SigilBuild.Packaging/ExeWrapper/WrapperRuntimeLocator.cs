using System;
using System.IO;

namespace SigilBuild.Packaging.ExeWrapper;

/// <summary>
/// Locates the AOT-published <c>SigilBuild.Wrapper.exe</c> runtime that
/// <see cref="ExeWrapperPackager"/> stamps with a step blob and payload.
/// </summary>
/// <remarks>
/// The runtime is supplied by build-time wiring: a CI step runs
/// <c>dotnet publish src/SigilBuild.Wrapper -c Release</c> and copies the
/// resulting binary into <c>bin/.../runtimes/win-x64/SigilBuild.Wrapper.exe</c>
/// alongside the SDK package. That copy lands in Task 13/14; for the Task 7
/// skeleton the integration test that exercises this path is marked
/// <c>[Fact(Skip = "...")]</c>.
/// </remarks>
internal static class WrapperRuntimeLocator
{
    // TODO(Task 14): wire build-time copy of AOT runtime into runtimes/win-x64/.
    public static string Locate()
    {
        var sdkRoot = AppContext.BaseDirectory;
        var candidate = Path.Combine(sdkRoot, "runtimes", "win-x64", "SigilBuild.Wrapper.exe");
        if (!File.Exists(candidate))
        {
            throw new FileNotFoundException(
                $"SigilBuild.Wrapper.exe not found at {candidate}; the AOT runtime " +
                "must be published and copied into the SDK package before pack time.",
                candidate);
        }
        return candidate;
    }
}
