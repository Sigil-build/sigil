namespace SigilBuild.Wrapper.IntegrationTests;

/// <summary>
/// Gates the multi-edition VM-style integration tests. The "VM" naming is
/// kept for continuity with the long-term plan (a Windows Sandbox harness);
/// for Sprint 5c / Task 13 the gate also keeps these out of the default
/// developer test loop, where launching the freshly-packed setup exe
/// against the host filesystem would require admin rights and the
/// AOT-published wrapper runtime to be staged under <c>runtimes/win-x64/</c>.
/// </summary>
internal static class TestEnvironment
{
    /// <summary>
    /// True when the host environment opts into the VM-style integration tests.
    /// Gated on <c>SIGIL_VM_TESTS=1</c> to avoid running these in stock developer
    /// setups or PR runs where they'd require admin rights / Windows Sandbox config.
    /// CI sets this in the <c>wrapper-vm-tests</c> job once enabled.
    /// </summary>
    public static bool IsEnabled =>
        System.Environment.GetEnvironmentVariable("SIGIL_VM_TESTS") == "1";

    /// <summary>
    /// True when the AOT-published installer-host runtime is staged next to the
    /// test assembly under <c>runtimes/win-x64/SigilBuild.Installer.Host.exe</c>
    /// (the exact name + layout <c>WrapperRuntimeLocator.Locate</c> resolves at
    /// pack time). Until <c>scripts/publish-installer-runtime.ps1</c> stages it,
    /// <c>ExeWrapperPackager</c> cannot produce a setup exe and the integration
    /// tests report a genuine <c>Skipped</c> result (register row R6 — they used
    /// to soft-skip via an early return that reported <c>Passed</c>; that was
    /// fixed in T1.1). (The legacy <c>SigilBuild.Wrapper.exe</c> name predates the
    /// T3 rename to the Avalonia host; it is accepted as a fallback for continuity.)
    /// </summary>
    public static bool IsRuntimeAvailable
    {
        get
        {
            try
            {
                var runtimeDir = System.IO.Path.Combine(
                    System.AppContext.BaseDirectory, "runtimes", "win-x64");
                return System.IO.File.Exists(
                           System.IO.Path.Combine(runtimeDir, "SigilBuild.Installer.Host.exe"))
                    || System.IO.File.Exists(
                           System.IO.Path.Combine(runtimeDir, "SigilBuild.Wrapper.exe"));
            }
            catch
            {
                return false;
            }
        }
    }
}
