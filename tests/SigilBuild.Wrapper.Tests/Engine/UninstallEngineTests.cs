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
            var uninstallResult = await new UninstallEngine().RunAsync(appId, dst.Path, InstallScope.User);
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
#pragma warning disable CA1031 // Test cleanup must never mask the original assertion failure.
            try { UninstallStateStore.Delete(appId, InstallScope.User); } catch { /* best-effort */ }
            try
            {
                Microsoft.Win32.Registry.LocalMachine.DeleteSubKeyTree(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + appId,
                    throwOnMissingSubKey: false);
            }
            catch
            {
                // best-effort
            }
#pragma warning restore CA1031
        }
    }

    [Fact]
    public async Task Uninstall_with_missing_state_returns_clear_error()
    {
        using var installDir = new TempDir();
        var result = await new UninstallEngine().RunAsync(
            "sigil.bogus." + Guid.NewGuid().ToString("N"), installDir.Path);
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
}
