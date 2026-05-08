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
    /// True when the AOT-published wrapper runtime is staged next to the test
    /// assembly. Until the SDK build pipeline copies <c>SigilBuild.Wrapper.exe</c>
    /// into <c>runtimes/win-x64/</c>, <see cref="ExeWrapperPackager"/> cannot
    /// produce a setup exe and the integration tests soft-skip.
    /// </summary>
    public static bool IsRuntimeAvailable
    {
        get
        {
            try
            {
                var sdkRoot = System.AppContext.BaseDirectory;
                var candidate = System.IO.Path.Combine(
                    sdkRoot, "runtimes", "win-x64", "SigilBuild.Wrapper.exe");
                return System.IO.File.Exists(candidate);
            }
            catch
            {
                return false;
            }
        }
    }
}
