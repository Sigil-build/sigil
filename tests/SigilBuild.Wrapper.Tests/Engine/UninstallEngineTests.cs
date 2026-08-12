namespace SigilBuild.Wrapper.Tests.Engine;

using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Tests.Helpers;
using Xunit;

public class UninstallEngineTests
{
    [Fact]
    public async Task Install_persists_journal_then_uninstall_replays_inverse()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var src = new TempDir();
        using var dst = new TempDir();
        File.WriteAllText(Path.Combine(src.Path, "x.txt"), "1");

        var appId = "sigil.test." + Guid.NewGuid().ToString("N");
        try
        {
            // Install: copy a file + create a directory, capturing both into
            // the journal. The InstallEngine returns the journal we'll persist.
            var steps = new InstallStep[]
            {
                new InstallStep.FileCopy(
                    "s1",
                    From: Path.Combine(src.Path, "*.txt"),
                    To: dst.Path,
                    Overwrite: true,
                    When: null,
                    OnFailure: OnFailure.Fail),
                new InstallStep.DirectoryCreate(
                    "s2",
                    Path: Path.Combine(dst.Path, "sub"),
                    When: null,
                    OnFailure: OnFailure.Fail),
            };
            var installResult = await new InstallEngine().RunAsync(steps, StepContext.Empty);
            installResult.Success.Should().BeTrue();
            File.Exists(Path.Combine(dst.Path, "x.txt")).Should().BeTrue();
            Directory.Exists(Path.Combine(dst.Path, "sub")).Should().BeTrue();

            // Persist journal — this is what Program.Main does on install success. The
            // install dir is recorded so the uninstall can anchor its replay to it (R1).
            UninstallStateStore.Save(
                appId, installResult.Journal, InstallScope.User,
                secretValues: null, progress: null, installDir: dst.Path);
            File.Exists(UninstallStateStore.PathFor(appId, InstallScope.User)).Should().BeTrue();

            // Uninstall: rehydrate + UndoAsync in reverse.
            var uninstallResult = await new UninstallEngine().RunAsync(appId, dst.Path, SignedDeclarations.None, InstallScope.User);
            uninstallResult.Success.Should().BeTrue();

            // Verify state is restored: copied file gone, created dir gone.
            File.Exists(Path.Combine(dst.Path, "x.txt")).Should().BeFalse();
            Directory.Exists(Path.Combine(dst.Path, "sub")).Should().BeFalse();

            // Uninstall state cleaned up.
            File.Exists(UninstallStateStore.PathFor(appId, InstallScope.User)).Should().BeFalse();
        }
        finally
        {
            // Best-effort cleanup if the test threw mid-flight.
            //
            // R57: this used to also DeleteSubKeyTree an HKLM\…\Uninstall key. Harmless
            // while no test creates that key — but CI runs elevated, so on an app-id
            // collision it was the one line in the suite that would do something real to
            // the host. This test installs to user scope and writes no HKLM row.
#pragma warning disable CA1031 // Test cleanup must never mask the original assertion failure.
            try { UninstallStateStore.Delete(appId, InstallScope.User); } catch { /* best-effort */ }
#pragma warning restore CA1031
        }
    }

    [Fact]
    public async Task Uninstall_with_missing_state_returns_clear_error()
    {
        using var installDir = new TempDir();
        var result = await new UninstallEngine().RunAsync(
            "sigil.bogus." + Guid.NewGuid().ToString("N"), installDir.Path, SignedDeclarations.None);
        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNull();
        result.Error!.Should().Contain("no uninstall state found");
    }

    [Fact]
    public async Task RollbackJournal_round_trips_via_serialization()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var dst = new TempDir();
        var appId = "sigil.roundtrip." + Guid.NewGuid().ToString("N");
        try
        {
            // Build a journal with two record types. We deliberately avoid
            // touching the registry / env here so the test stays sandboxed.
            var journal = new RollbackJournal();
            journal.Append(new RollbackRecord.RestoreFile(
                Path.Combine(dst.Path, "f.txt"), ExistedBefore: false, BackupPath: null));
            journal.Append(new RollbackRecord.RemoveDirectory(
                Path.Combine(dst.Path, "d")));

            UninstallStateStore.Save(appId, journal, InstallScope.User);
            var loaded = UninstallStateStore.TryLoad(appId, InstallScope.User);
            loaded.Should().NotBeNull();
            loaded!.Journal.Records.Should().HaveCount(2);
            loaded.Journal.Records[0].Should().BeOfType<RollbackRecord.RestoreFile>();
            loaded.Journal.Records[1].Should().BeOfType<RollbackRecord.RemoveDirectory>();
            loaded.Scope.Should().Be(InstallScope.User);

            var restoreFile = (RollbackRecord.RestoreFile)loaded.Journal.Records[0];
            restoreFile.Path.Should().Be(Path.Combine(dst.Path, "f.txt"));
            restoreFile.ExistedBefore.Should().BeFalse();
            restoreFile.BackupPath.Should().BeNull();

            var removeDir = (RollbackRecord.RemoveDirectory)loaded.Journal.Records[1];
            removeDir.Path.Should().Be(Path.Combine(dst.Path, "d"));
        }
        finally
        {
#pragma warning disable CA1031 // Test cleanup must never mask the original assertion failure.
            try { UninstallStateStore.Delete(appId, InstallScope.User); } catch { /* best-effort */ }
#pragma warning restore CA1031
        }
    }

    // ---- R15: an uninstall must not claim a success it did not achieve ----------------

    /// <summary>
    /// R15. The negative test. A journal record whose undo cannot succeed must make the
    /// uninstall report failure AND leave <c>uninstall.json</c> on disk, because that
    /// file is the only description of what is left to remove — deleting it after a
    /// failed replay is what turned a partial uninstall into a permanently installed app.
    /// </summary>
    /// <remarks>
    /// The record is a <c>restore_file</c> whose destination is an existing DIRECTORY:
    /// <c>File.Copy</c> cannot overwrite one, so the undo throws. That is the same shape
    /// as every real "the file was locked / access was denied" partial uninstall, and it
    /// is deterministic on every OS without creating a service, a scheduled task, a
    /// firewall rule or any other host object — which matters because CI runs elevated.
    /// </remarks>
    [Fact]
    public async Task Uninstall_that_cannot_reverse_a_record_reports_failure_and_keeps_the_state_for_retry()
    {
        using var installDir = new TempDir();
        var appId = "sigil.test." + Guid.NewGuid().ToString("N");
        try
        {
            var blocked = Path.Combine(installDir.Path, "blocked");
            Directory.CreateDirectory(blocked);
            var backup = Path.Combine(installDir.Path, "blocked.sigil-bak");
            File.WriteAllText(backup, "the content the undo would restore");

            var journal = new RollbackJournal();
            journal.Append(new RollbackRecord.RestoreFile(
                blocked, ExistedBefore: true, BackupPath: backup));

            UninstallStateStore.Save(
                appId, journal, InstallScope.User,
                secretValues: null, progress: null, installDir: installDir.Path);

            var result = await new UninstallEngine().RunAsync(appId, installDir.Path, SignedDeclarations.None, InstallScope.User);

            result.Success.Should().BeFalse(
                "a replay in which a record could not be reversed did not achieve what an " +
                "Ok result claims — R15");
            result.Error.Should().NotBeNull();
            result.Error!.Should().Contain("could not be reversed");

            File.Exists(UninstallStateStore.PathFor(appId, InstallScope.User)).Should().BeTrue(
                "the state file is the only record of what is left to remove; deleting it " +
                "after a failed uninstall leaves the app permanently installed with no retry");
        }
        finally
        {
#pragma warning disable CA1031 // Test cleanup must never mask the original assertion failure.
            try { UninstallStateStore.Delete(appId, InstallScope.User); } catch { /* best-effort */ }
#pragma warning restore CA1031
        }
    }

    /// <summary>
    /// R15, the other direction. A clean replay must still delete the state — retention
    /// is for failures only, and a retained state file on the happy path would leave a
    /// stale ARP row behind on every uninstall.
    /// </summary>
    [Fact]
    public async Task Clean_uninstall_still_deletes_the_state()
    {
        using var installDir = new TempDir();
        var appId = "sigil.test." + Guid.NewGuid().ToString("N");
        try
        {
            var f = Path.Combine(installDir.Path, "f.txt");
            File.WriteAllText(f, "x");

            var journal = new RollbackJournal();
            journal.Append(new RollbackRecord.RestoreFile(f, ExistedBefore: false, BackupPath: null));

            UninstallStateStore.Save(
                appId, journal, InstallScope.User,
                secretValues: null, progress: null, installDir: installDir.Path);

            var result = await new UninstallEngine().RunAsync(appId, installDir.Path, SignedDeclarations.None, InstallScope.User);

            result.Success.Should().BeTrue(result.Error ?? "clean replay");
            File.Exists(f).Should().BeFalse();
            File.Exists(UninstallStateStore.PathFor(appId, InstallScope.User)).Should().BeFalse();
        }
        finally
        {
#pragma warning disable CA1031 // Test cleanup must never mask the original assertion failure.
            try { UninstallStateStore.Delete(appId, InstallScope.User); } catch { /* best-effort */ }
#pragma warning restore CA1031
        }
    }

    /// <summary>
    /// R15's over-refusal guard, and the reason the three process-driven undos check the
    /// END STATE rather than the exit code. <c>sc delete</c>, <c>schtasks /Delete</c> and
    /// <c>netsh … delete rule</c> ALL exit non-zero when the object does not exist. If a
    /// non-zero exit were the failure signal, every uninstall of an app whose service /
    /// task / rule a user had already removed would report failure and retain state
    /// forever — a survivable uninstall turned into an unremovable app, which is the
    /// exact harm R15 exists to prevent.
    /// </summary>
    /// <remarks>
    /// Asserts on the journal outcome only. Nothing here creates a service, a scheduled
    /// task or a firewall rule — the names are fresh GUIDs that cannot exist, and CI runs
    /// elevated, so a test that created one would create it on the runner for real.
    /// </remarks>
    [Fact]
    public async Task Undo_of_an_already_absent_service_task_or_rule_is_not_a_failure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var nonce = Guid.NewGuid().ToString("N");
        var journal = new RollbackJournal();
        journal.Append(new RollbackRecord.RemoveService("sigil-absent-svc-" + nonce));
        journal.Append(new RollbackRecord.DeleteScheduledTask(@"\Sigil\absent-task-" + nonce));
        journal.Append(new RollbackRecord.DeleteFirewallRule("sigil-absent-rule-" + nonce));

        var outcome = await journal.UndoAsync(ReplayAnchorage.InProcess);

        outcome.FailedRecords.Should().BeEmpty(
            "an object that does not exist already satisfies 'no service / task / rule " +
            "after rollback'; treating the tool's non-zero 'not found' exit as a failure " +
            "would refuse legitimate uninstalls");
        outcome.IsClean.Should().BeTrue();
    }
}
