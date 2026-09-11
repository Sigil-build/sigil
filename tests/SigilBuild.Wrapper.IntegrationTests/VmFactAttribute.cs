using System;
using Xunit;

namespace SigilBuild.Wrapper.IntegrationTests;

/// <summary>
/// Reports a genuine Skipped result when the base VM-integration preconditions —
/// Windows, <c>SIGIL_VM_TESTS=1</c>, and the staged installer-host runtime — are
/// absent, instead of returning early and reporting as Passed (R6).
/// Used by <see cref="MultiEditionInstallTests"/>,
/// <see cref="WixClassInstallUninstallTests"/>, and
/// <see cref="LocalizationEndToEndTests"/>, whose <c>ShouldRun</c> gate is exactly
/// this combination.
/// </summary>
internal sealed class VmFactAttribute : FactAttribute
{
    public VmFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "VM integration test: requires Windows";
        }
        else if (!TestEnvironment.IsEnabled)
        {
            Skip = "VM integration test: SIGIL_VM_TESTS is not set to 1";
        }
        else if (!TestEnvironment.IsRuntimeAvailable)
        {
            Skip = "VM integration test: staged installer-host runtime not found "
                 + "(run scripts/publish-installer-runtime.ps1)";
        }
    }
}

/// <summary>
/// Reports a genuine Skipped result when the base VM preconditions are met but
/// <c>SIGIL_VM_UPGRADE=1</c> is not set — the extra opt-in
/// <see cref="UpgradeInstallTests"/> requires on top of <see cref="VmFactAttribute"/>'s
/// checks (R6).
/// </summary>
internal sealed class VmUpgradeFactAttribute : FactAttribute
{
    public VmUpgradeFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "VM integration test: requires Windows";
        }
        else if (!TestEnvironment.IsEnabled)
        {
            Skip = "VM integration test: SIGIL_VM_TESTS is not set to 1";
        }
        else if (Environment.GetEnvironmentVariable("SIGIL_VM_UPGRADE") != "1")
        {
            Skip = "VM integration test: SIGIL_VM_UPGRADE is not set to 1";
        }
        else if (!TestEnvironment.IsRuntimeAvailable)
        {
            Skip = "VM integration test: staged installer-host runtime not found "
                 + "(run scripts/publish-installer-runtime.ps1)";
        }
    }
}

/// <summary>
/// <see cref="VmUpgradeFactAttribute"/> plus one more precondition: the process must
/// NOT be elevated. The upgrade assertions that need this can only be true when the
/// install can see its own prior per-user install — which, in an elevated session, it
/// cannot.
/// </summary>
/// <remarks>
/// <c>InstalledStateResolver.ScopeProbeOrder</c> probes HKLM only when the process is
/// elevated (R2): an elevated process must not act on HKCU-sourced data, in particular an
/// attacker-plantable <c>UninstallString</c> it would then spawn as administrator. Do not
/// weaken this for a test. Consequence: the v2 ARP row lives in HKCU, so
/// <c>UpgradePlanner.Plan</c> short-circuits on <c>!state.Found</c> and returns
/// <c>FreshInstall</c> — never <c>DowngradeBlocked</c>, with no <c>priorInstallDir</c> to
/// preserve. Every GitHub-hosted Windows runner is an elevated <c>runneradmin</c> session,
/// and a <c>/currentuser</c> install does not de-elevate, so on CI the downgrade guard is
/// silently off; <c>/allusers</c> is not a substitute, since machine scope moves the R3
/// containment roots to <c>%ProgramFiles%</c>, which rejects the temp-rooted fixture
/// <c>install_dir</c>. These assertions report a genuine Skip (R6) naming R2 and the
/// elevated per-user upgrade-plan row — the divergence where the plan is ARP-sourced and
/// blind under elevation while <c>PerformReinstallCleanupAsync</c> is state-store-sourced
/// and sighted, so an elevated per-user run believes it is a fresh install and still tears
/// the prior version down — rather than passing or failing for the wrong reason. The pure
/// decision table stays covered everywhere by <c>UpgradePlannerTests</c>,
/// <c>InstallDirResolverTests</c> and <c>UpgradeSessionTests</c>.
/// </remarks>
internal sealed class VmUpgradeUnelevatedFactAttribute : FactAttribute
{
    public VmUpgradeUnelevatedFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "VM integration test: requires Windows";
        }
        else if (!TestEnvironment.IsEnabled)
        {
            Skip = "VM integration test: SIGIL_VM_TESTS is not set to 1";
        }
        else if (Environment.GetEnvironmentVariable("SIGIL_VM_UPGRADE") != "1")
        {
            Skip = "VM integration test: SIGIL_VM_UPGRADE is not set to 1";
        }
        else if (!TestEnvironment.IsRuntimeAvailable)
        {
            Skip = "VM integration test: staged installer-host runtime not found "
                 + "(run scripts/publish-installer-runtime.ps1)";
        }
        else if (SigilBuild.Wrapper.Engine.Elevation.IsProcessElevated())
        {
            Skip = "VM integration test: the process is ELEVATED, and an elevated "
                 + "per-user install cannot see its own prior per-user install — "
                 + "InstalledStateResolver.ScopeProbeOrder probes HKLM only when "
                 + "elevated (R2, lane S1: an elevated process must not act on "
                 + "HKCU-sourced data), so the prior version's HKCU ARP row is invisible "
                 + "to UpgradePlanner and the plan is FreshInstall. This assertion "
                 + "cannot hold here and would pass or fail for the wrong reason; it is "
                 + "skipped rather than weakened, and the divergence it exposes (the "
                 + "plan is ARP-sourced and blind while the reinstall cleanup is "
                 + "state-store-sourced and sighted) is filed as the elevated per-user "
                 + "upgrade-plan row. Run this leg UNELEVATED to exercise it.";
        }
    }
}

/// <summary>
/// Reports a genuine Skipped result when the base VM preconditions are met but
/// <c>SIGIL_VM_UNINSTALL_SURVIVE=1</c> is not set — the extra opt-in
/// <see cref="ArpUninstallStringTests"/> requires on top of
/// <see cref="VmFactAttribute"/>'s checks (R6).
/// </summary>
/// <remarks>
/// <c>wrapper-vm-tests.yml</c> declares <c>SIGIL_VM_UNINSTALL_SURVIVE</c> as a scenario
/// toggle; this attribute is what consumes it (R58).
/// </remarks>
internal sealed class VmUninstallSurviveFactAttribute : FactAttribute
{
    public VmUninstallSurviveFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "VM integration test: requires Windows";
        }
        else if (!TestEnvironment.IsEnabled)
        {
            Skip = "VM integration test: SIGIL_VM_TESTS is not set to 1";
        }
        else if (Environment.GetEnvironmentVariable("SIGIL_VM_UNINSTALL_SURVIVE") != "1")
        {
            Skip = "VM integration test: SIGIL_VM_UNINSTALL_SURVIVE is not set to 1";
        }
        else if (!TestEnvironment.IsRuntimeAvailable)
        {
            Skip = "VM integration test: staged installer-host runtime not found "
                 + "(run scripts/publish-installer-runtime.ps1)";
        }
    }
}

/// <summary>
/// Reports a genuine Skipped result when the base VM preconditions are met but
/// <c>SIGIL_VM_PREREQ=1</c> is not set — the extra opt-in
/// <see cref="PrerequisiteInstallTests"/> requires on top of
/// <see cref="VmFactAttribute"/>'s checks (R6).
/// </summary>
internal sealed class VmPrerequisiteFactAttribute : FactAttribute
{
    public VmPrerequisiteFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "VM integration test: requires Windows";
        }
        else if (!TestEnvironment.IsEnabled)
        {
            Skip = "VM integration test: SIGIL_VM_TESTS is not set to 1";
        }
        else if (Environment.GetEnvironmentVariable("SIGIL_VM_PREREQ") != "1")
        {
            Skip = "VM integration test: SIGIL_VM_PREREQ is not set to 1";
        }
        else if (!TestEnvironment.IsRuntimeAvailable)
        {
            Skip = "VM integration test: staged installer-host runtime not found "
                 + "(run scripts/publish-installer-runtime.ps1)";
        }
    }
}

/// <summary>
/// Reports a genuine Skipped result when the elevated system-steps VM preconditions
/// are absent: Windows, <c>SIGIL_VM_TESTS=1</c>, <c>SIGIL_VM_SYSTEMSTEPS=1</c>, and
/// process elevation (<see cref="Elevation.IsProcessElevated"/>). Note this precondition
/// set does NOT include <see cref="TestEnvironment.IsRuntimeAvailable"/> — these tests
/// (<see cref="ScheduledTaskCreateInstallTests"/>, <see cref="FirewallRuleInstallTests"/>,
/// <see cref="ComRegisterInstallTests"/>) drive the step classes directly rather than
/// through a packed Setup.exe, so no staged runtime is required; they instead require
/// admin rights, which is why they get their own attribute rather than reusing
/// <see cref="VmFactAttribute"/> (R6).
/// </summary>
internal sealed class VmSystemStepsFactAttribute : FactAttribute
{
    public VmSystemStepsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "VM integration test: requires Windows";
        }
        else if (!TestEnvironment.IsEnabled)
        {
            Skip = "VM integration test: SIGIL_VM_TESTS is not set to 1";
        }
        else if (Environment.GetEnvironmentVariable("SIGIL_VM_SYSTEMSTEPS") != "1")
        {
            Skip = "VM integration test: SIGIL_VM_SYSTEMSTEPS is not set to 1";
        }
        else if (!SigilBuild.Wrapper.Engine.Elevation.IsProcessElevated())
        {
            Skip = "VM integration test: process is not elevated (run as administrator)";
        }
    }
}
