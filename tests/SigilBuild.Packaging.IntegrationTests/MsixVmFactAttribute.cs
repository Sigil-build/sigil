namespace SigilBuild.Packaging.IntegrationTests;

using System;

/// <summary>
/// Reports a genuine Skipped result when the MSIX VM-style install preconditions —
/// Windows and <c>SIGIL_MSIX_VM_TESTS=1</c> — are absent, instead of returning early
/// and reporting as Passed (register row R6). Used by
/// <see cref="MsixInstallSmokeTests.Pack_and_install_unsigned_msix_via_AddAppxPackage_succeeds"/>.
/// </summary>
internal sealed class MsixVmFactAttribute : FactAttribute
{
    public MsixVmFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "MSIX VM install test: requires Windows";
        }
        else if (Environment.GetEnvironmentVariable("SIGIL_MSIX_VM_TESTS") != "1")
        {
            Skip = "MSIX VM install test: SIGIL_MSIX_VM_TESTS is not set to 1";
        }
    }
}
