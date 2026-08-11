using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using SigilBuild.Wrapper.Engine;

namespace SigilBuild.Packaging.Tests;

internal static class TestAssemblySetup
{
    /// <summary>
    /// Never let this assembly take <c>SecureStaging</c>'s machine-wide (elevated) siting,
    /// whatever token the test host happens to hold.
    /// </summary>
    /// <remarks>
    /// CI runs <b>elevated</b>, and this assembly drives the web-installer stub end to end
    /// through a real <c>InstallSession</c> — which builds its own <c>StepContext</c> and
    /// offers no seam of its own. Without this floor, resolving the stub's
    /// <c>{staging_dir}</c> would create a directory in the real <c>%ProgramData%</c> and
    /// execute the downloaded "package" out of it on every CI run. Individual tests still
    /// pin their own scratch root where they assert on the path; this makes the safe answer
    /// the default so a future test cannot lose it by omission.
    /// </remarks>
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255", Justification = "Test-assembly bootstrap: pins staging away from the real %ProgramData% before any test runs.")]
    internal static void KeepStagingOutOfProgramData() => SecureStaging.NeverStageElevatedForTesting();
}
