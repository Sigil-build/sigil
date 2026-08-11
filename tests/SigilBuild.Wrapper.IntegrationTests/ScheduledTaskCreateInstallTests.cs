namespace SigilBuild.Wrapper.IntegrationTests;

using System;
using System.IO;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Steps;
using Xunit;

/// <summary>
/// T13.1 (P13): the live create + reverse leg for <c>scheduled_task_create</c>
/// (P11 / T11.1), deferred to CI-VM when that step shipped with unit/parse/
/// roundtrip coverage only. Drives <see cref="ScheduledTaskCreateStep"/>
/// directly (the argument construction itself is already proven byte-for-byte
/// by <c>ScheduledTaskCreateStepTests.BuildCreateArgs_*</c>; this test is only
/// about the real <c>schtasks.exe</c> round trip) and asserts BOTH halves of
/// the P11 "Verify" block:
/// <list type="bullet">
/// <item><description>create → <c>schtasks /Query /TN &lt;name&gt;</c> finds it;</description></item>
/// <item><description>reverse (the journaled <see cref="RollbackRecord.DeleteScheduledTask"/>,
/// the same record <c>setup.exe /Uninstall</c> and a mid-install crash both
/// invoke) → the task is gone.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>Gating:</b> reports a genuine Skipped result (via
/// <see cref="VmSystemStepsFactAttribute"/>, register row R6 — the same convention
/// as <c>PrerequisiteInstallTests</c>/<c>UpgradeInstallTests</c>) unless the
/// host is Windows, <c>SIGIL_VM_TESTS=1</c> and <c>SIGIL_VM_SYSTEMSTEPS=1</c>
/// are both set, AND the current process is elevated
/// (<see cref="Elevation.IsProcessElevated"/>) — <c>schtasks /Create /RU
/// SYSTEM</c> requires admin. This is NOT run locally in this sandbox (not
/// Windows, not elevated, env vars unset) — the CI VM job
/// (<c>p11-system-steps-vm</c> in <c>wrapper-vm-tests.yml</c>) sets all three
/// and runs on a real elevated <c>windows-latest</c> runner.
/// </para>
/// <para>
/// Uses a uniquely-named <c>SigilItTask_*</c> task (per-run GUID suffix) and a
/// benign, always-present program path (<c>cmd.exe</c> — never actually
/// launched by this test, only referenced by the task definition) so repeat
/// runs never collide and never depend on a fixture DLL. A <c>finally</c>
/// best-effort <c>schtasks /Delete /F</c> guarantees the task doesn't survive
/// a failed assertion mid-test.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public class ScheduledTaskCreateInstallTests
{
    [VmSystemStepsFact]
    public async Task Create_then_reverse_scheduled_task_round_trip()
    {
        var taskName = $"SigilItTask_{Guid.NewGuid():N}";
        var program = Path.Combine(Environment.SystemDirectory, "cmd.exe");

        try
        {
            var spec = new InstallStep.ScheduledTaskCreate(
                Id: "it-schtask",
                Name: taskName,
                Program: program,
                Arguments: null,
                Trigger: "onstart",
                RunLevel: "limited",
                When: null,
                OnFailure: OnFailure.Continue);
            var journal = new RollbackJournal();

            var result = await new ScheduledTaskCreateStep(spec)
                .RunAsync(StepContext.Empty, journal, default);
            result.Success.Should().BeTrue(result.Error ?? "schtasks /Create should succeed under elevation");

            var afterCreate = await SystemStepProcessRunner
                .RunAsync("schtasks.exe", "/Query", "/TN", taskName);
            afterCreate.ExitCode.Should().Be(0,
                $"schtasks /Query must find '{taskName}' right after create. stderr: {afterCreate.Stderr}");
            afterCreate.Stdout.Should().Contain(taskName);

            // Reverse via the SAME rollback record setup.exe /Uninstall and a
            // mid-install crash both invoke — proving the create+reverse pair,
            // not just the create half.
            journal.Records.Should().ContainSingle()
                .Which.Should().BeOfType<RollbackRecord.DeleteScheduledTask>()
                .Which.TaskName.Should().Be(taskName);
            await journal.Records[0].UndoAsync(default);

            var afterUndo = await SystemStepProcessRunner
                .RunAsync("schtasks.exe", "/Query", "/TN", taskName);
            afterUndo.ExitCode.Should().NotBe(0, "schtasks /Query must NOT find the task after rollback");
        }
        finally
        {
            await SystemStepProcessRunner
                .BestEffortAsync("schtasks.exe", "/Delete", "/TN", taskName, "/F");
        }
    }
}
