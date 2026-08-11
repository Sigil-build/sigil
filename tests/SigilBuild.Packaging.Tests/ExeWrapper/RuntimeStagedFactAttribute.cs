using System;
using System.IO;
using Xunit;

namespace SigilBuild.Packaging.Tests.ExeWrapper;

/// <summary>
/// Reports a genuine Skipped result when the staged Native-AOT installer-host
/// runtime preconditions are absent, instead of writing
/// <c>Console.WriteLine("SKIP: ...")</c> and returning early — which reports as
/// Passed and never reaches the trx summary at all (register row R6).
/// </summary>
/// <remarks>
/// The runtime is expected to be pre-staged under
/// <c>runtimes/win-x64/SigilBuild.Installer.Host.exe</c> (next to the test
/// assembly) by <c>scripts/publish-installer-runtime.ps1</c>. This deliberately
/// does NOT trigger an on-demand AOT publish — that keeps the normal
/// <c>dotnet test</c> fast and free of the slow AOT link — so it skips
/// gracefully when the runtime has not been staged.
/// </remarks>
internal sealed class RuntimeStagedFactAttribute : FactAttribute
{
    public RuntimeStagedFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Packaging test: requires Windows";
        }
        else if (!IsRuntimeStaged())
        {
            Skip = "Packaging test: staged installer-host runtime not found "
                 + "(run scripts/publish-installer-runtime.ps1)";
        }
    }

    private static bool IsRuntimeStaged()
    {
        var staged = Path.Combine(
            AppContext.BaseDirectory, "runtimes", "win-x64", "SigilBuild.Installer.Host.exe");
        return File.Exists(staged);
    }
}
