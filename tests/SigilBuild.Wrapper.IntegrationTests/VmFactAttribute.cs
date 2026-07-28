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
