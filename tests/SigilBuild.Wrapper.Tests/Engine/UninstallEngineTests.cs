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

            // Persist journal — this is what Program.Main does on install success.
            UninstallStateStore.Save(appId, installResult.Journal);
            File.Exists(UninstallStateStore.PathFor(appId)).Should().BeTrue();

            // Uninstall: rehydrate + UndoAsync in reverse.
            var uninstallResult = await new UninstallEngine().RunAsync(appId);
            uninstallResult.Success.Should().BeTrue();

            // Verify state is restored: copied file gone, created dir gone.
            File.Exists(Path.Combine(dst.Path, "x.txt")).Should().BeFalse();
            Directory.Exists(Path.Combine(dst.Path, "sub")).Should().BeFalse();

            // Uninstall state cleaned up.
            File.Exists(UninstallStateStore.PathFor(appId)).Should().BeFalse();
        }
        finally
        {
            // Best-effort cleanup if the test threw mid-flight.
#pragma warning disable CA1031 // Test cleanup must never mask the original assertion failure.
            try { UninstallStateStore.Delete(appId); } catch { /* best-effort */ }
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
        var result = await new UninstallEngine().RunAsync(
            "sigil.bogus." + Guid.NewGuid().ToString("N"));
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

            UninstallStateStore.Save(appId, journal);
            var loaded = UninstallStateStore.TryLoad(appId);
            loaded.Should().NotBeNull();
            loaded!.Records.Should().HaveCount(2);
            loaded.Records[0].Should().BeOfType<RollbackRecord.RestoreFile>();
            loaded.Records[1].Should().BeOfType<RollbackRecord.RemoveDirectory>();

            var restoreFile = (RollbackRecord.RestoreFile)loaded.Records[0];
            restoreFile.Path.Should().Be(Path.Combine(dst.Path, "f.txt"));
            restoreFile.ExistedBefore.Should().BeFalse();
            restoreFile.BackupPath.Should().BeNull();

            var removeDir = (RollbackRecord.RemoveDirectory)loaded.Records[1];
            removeDir.Path.Should().Be(Path.Combine(dst.Path, "d"));
        }
        finally
        {
#pragma warning disable CA1031 // Test cleanup must never mask the original assertion failure.
            try { UninstallStateStore.Delete(appId); } catch { /* best-effort */ }
#pragma warning restore CA1031
        }
    }
}
