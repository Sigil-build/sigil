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
/// Uses a uniquely-named <c>SigilItTask_*</c> task (per-run GUID suffix) so
/// repeat runs never collide, and a <c>finally</c> best-effort <c>schtasks
/// /Delete /F</c> guarantees the task doesn't survive a failed assertion
/// mid-test.
/// </para>
/// <para>
/// <b>The task's <c>program</c> now lives inside a real <c>install_dir</c>
/// (register row R67).</b> As written in P11 this leg ran against
/// <see cref="StepContext.Empty"/> and pointed <c>/TR</c> at
/// <c>%SystemRoot%\System32\cmd.exe</c> — "always present, never actually
/// launched". Stage 1 lane S2 (rows R3/R9/R16) then anchored every SYSTEM-level
/// step target to the run's resolved <c>install_dir</c>, and the matrix's first
/// real run refused the step exactly as designed: <c>/RU SYSTEM</c> plus a target
/// outside any install directory is the R3 attack, and a run with no resolved
/// <c>install_dir</c> has no anchor to check against at all. So the harness now
/// resolves a genuine machine-scope <c>install_dir</c>
/// (<see cref="SystemStepInstallDir"/>, the same
/// <c>/D=</c> → <see cref="InstallDirResolver"/> path <c>InstallSession</c> takes)
/// and <b>copies</b> <c>cmd.exe</c> into it under the app-binary name the task
/// then targets — the <c>file_copy</c>-then-privileged-step ordering
/// <c>docs/guides/install-steps.md</c> prescribes. Every assertion below is the
/// one P11 wrote; only the anchor and the target's location changed. The copy is
/// still never launched: <c>onstart</c> fires at boot and the task is deleted
/// inside this test.
/// </para>
/// <para>
/// The copied path also happens to carry a space (<c>C:\Program Files\…</c>),
/// which the original <c>C:\Windows\system32\cmd.exe</c> did not — so this leg now
/// covers <c>BuildCreateArgs</c>' <c>/TR</c> quoting against a real
/// <c>schtasks.exe</c> parse rather than only in unit tests.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public class ScheduledTaskCreateInstallTests
{
    [VmSystemStepsFact]
    public async Task Create_then_reverse_scheduled_task_round_trip()
    {
        var taskName = $"SigilItTask_{Guid.NewGuid():N}";
        using var installDir = SystemStepInstallDir.CreateElevated();

        // The "app binary" this installer shipped: a copy of cmd.exe inside
        // install_dir. Anchored (inside install_dir, no junction on the way down)
        // and admin-only writable, which is what /RU SYSTEM requires of its target.
        var program = installDir.CopyIn(
            Path.Combine(Environment.SystemDirectory, "cmd.exe"), "SigilItHeartbeat.exe");

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
                .RunAsync(installDir.Context, journal, default);
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
