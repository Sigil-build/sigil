using System;
using Xunit;

namespace SigilBuild.Wrapper.IntegrationTests;

/// <summary>
/// Reports a genuine Skipped result when the base VM-integration preconditions —
/// Windows, <c>SIGIL_VM_TESTS=1</c>, and the staged installer-host runtime — are
/// absent, instead of returning early and reporting as Passed (register row R6).
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
/// checks (register row R6).
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
/// NOT be elevated. For the upgrade assertions that can only be true when the install
/// can see its own prior per-user install — which, in an elevated session, it cannot.
/// </summary>
/// <remarks>
/// <para><b>Why.</b> <c>InstalledStateResolver.ScopeProbeOrder</c> (register row
/// <b>R2</b>, lane S1) probes <b>HKLM only</b> when the process is elevated: an elevated
/// process must not act on HKCU-sourced data, in particular an attacker-plantable
/// <c>UninstallString</c> it would then spawn as administrator. That is deliberate and
/// must not be weakened for a test. Its consequence here is that an elevated per-user
/// install cannot see its own prior per-user install: the v2 ARP row lives in HKCU, so
/// <c>UpgradePlanner.Plan</c> short-circuits on <c>!state.Found</c> and returns
/// <c>FreshInstall</c> — never <c>DowngradeBlocked</c>, and with no <c>priorInstallDir</c>
/// to preserve.</para>
/// <para><b>Why that matters on CI.</b> Every GitHub-hosted Windows runner is an
/// elevated <c>runneradmin</c> session, and a <c>/currentuser</c> install does not
/// de-elevate (<c>InstallSession.RequiresElevation</c> is
/// <c>scope == Machine &amp;&amp; !elevated</c>), so the install runs in whatever
/// integrity level it was launched at. The downgrade guard is therefore silently off:
/// <c>Silent_downgrade_is_blocked_with_exit_code_3</c> gets <b>0</b> instead of 3, and
/// the prior-install-dir half of <c>Upgrade_replaces_older_version_…</c> lands in v2's
/// own default dir. Neither is a product defect the test can assert around, and
/// <c>/allusers</c> is not a substitute — machine scope moves the R3 containment roots
/// to <c>%ProgramFiles%</c>, which would reject the temp-rooted fixture
/// <c>install_dir</c> with exit 1.</para>
/// <para><b>Skip, not a vacuous pass</b> (register row <b>R6</b>): the reason names R2
/// and the register row being filed for the plan-vs-cleanup divergence it exposes — the
/// <em>elevated per-user upgrade-plan row</em>, whose number the docs lane is assigning.
/// The divergence: the plan is ARP-sourced and blind under elevation, while
/// <c>ExistingInstallDetected</c> → <c>PerformReinstallCleanupAsync</c> is state-store-
/// sourced and sighted, so an elevated per-user run believes it is a fresh install and
/// still tears the prior version down. The pure decision table stays covered by
/// <c>UpgradePlannerTests</c>, <c>InstallDirResolverTests</c> and
/// <c>UpgradeSessionTests</c>, which run everywhere; de-elevating the install-matrix leg
/// (it needs no admin — the P11 system-step legs are a separate job) would restore the
/// end-to-end coverage, and is a change for the workflow's owner.</para>
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
/// <see cref="VmFactAttribute"/>'s checks (register row R6).
/// </summary>
/// <remarks>
/// R58: <c>wrapper-vm-tests.yml</c> has declared <c>SIGIL_VM_UNINSTALL_SURVIVE</c> as a
/// T15 scenario toggle since T17 consolidated the matrix, but until now no test read it —
/// the leg was advertised and absent, which is a large part of why the ARP uninstall path
/// went unexercised and R58 stayed latent from P6 to the release candidate. This
/// attribute is what finally consumes it.
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
/// <see cref="VmFactAttribute"/>'s checks (register row R6).
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
/// <see cref="VmFactAttribute"/> (register row R6).
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
