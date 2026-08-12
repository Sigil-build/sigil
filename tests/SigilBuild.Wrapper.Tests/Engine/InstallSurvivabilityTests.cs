namespace SigilBuild.Wrapper.Tests.Engine;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Cli;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Tests.Helpers;
using Xunit;

/// <summary>
/// T15 uninstall survivability: the running installer is copied into the install
/// dir as <c>uninstall.exe</c>, the copy is journaled (so uninstall / rollback
/// reverse it), the ARP <c>UninstallString</c> targets that copy rather than the
/// downloaded setup exe, and the self-deletion record tolerates the running image.
/// </summary>
public sealed class InstallSurvivabilityTests
{
    [Fact]
    public void CopyUninstaller_copies_source_to_uninstall_exe_and_journals_removal()
    {
        using var src = new TempDir();
        using var installDir = new TempDir();

        var source = Path.Combine(src.Path, "MyApp-1.0-Setup.exe");
        File.WriteAllBytes(source, new byte[] { 1, 2, 3, 4 });

        var journal = new RollbackJournal();
        var dest = InstallSurvivability.CopyUninstaller(journal, source, installDir.Path);

        // The copy lands at {installDir}\uninstall.exe with identical bytes.
        Path.GetFileName(dest).Should().Be("uninstall.exe");
        Path.GetDirectoryName(dest).Should().Be(installDir.Path);
        File.Exists(dest).Should().BeTrue();
        File.ReadAllBytes(dest).Should().Equal(File.ReadAllBytes(source));

        // Exactly one journal record: the RemoveUninstaller for the copied file.
        journal.Records.Should().ContainSingle()
            .Which.Should().BeOfType<RollbackRecord.RemoveUninstaller>()
            .Which.Path.Should().Be(dest);
    }

    [Fact]
    public async Task Journal_undo_deletes_the_uninstaller_copy_when_not_the_running_image()
    {
        using var src = new TempDir();
        using var installDir = new TempDir();

        var source = Path.Combine(src.Path, "setup.exe");
        File.WriteAllBytes(source, new byte[] { 9 });

        var journal = new RollbackJournal();
        var dest = InstallSurvivability.CopyUninstaller(journal, source, installDir.Path);
        File.Exists(dest).Should().BeTrue();

        // The copy is NOT this test host's image, so undo deletes it outright.
        await journal.UndoAsync(ReplayAnchorage.InProcess);

        File.Exists(dest).Should().BeFalse("undoing the RemoveUninstaller record deletes the copy");
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void Uninstall_string_targets_the_copied_uninstaller_not_the_setup_exe()
    {
        using var src = new TempDir();
        using var installDir = new TempDir();

        var setupExe = Path.Combine(src.Path, "Downloaded-Setup.exe");
        File.WriteAllBytes(setupExe, new byte[] { 0 });

        var journal = new RollbackJournal();
        var dest = InstallSurvivability.CopyUninstaller(journal, setupExe, installDir.Path);

        var uninstallString = ArpRegistration.BuildUninstallString(dest, InstallScope.User);

        uninstallString.Should().Contain("uninstall.exe");
        uninstallString.Should().Contain("/S /Uninstall /currentuser");
        // The critical T15 guarantee: ARP does NOT point at the (deletable) download.
        uninstallString.Should().NotContain("Downloaded-Setup.exe");
    }

    [Fact]
    public void SelfDelete_removes_a_file_that_is_not_the_running_image()
    {
        using var dir = new TempDir();
        var target = Path.Combine(dir.Path, "uninstall.exe");
        File.WriteAllBytes(target, new byte[] { 7 });

        var outcome = SelfDelete.Remove(target, runningImagePath: Path.Combine(dir.Path, "something-else.exe"));

        outcome.Should().Be(SelfDeleteOutcome.Deleted);
        File.Exists(target).Should().BeFalse();
    }

    [Fact]
    public void SelfDelete_tolerates_the_running_image_and_does_not_delete_it_now()
    {
        using var dir = new TempDir();
        var target = Path.Combine(dir.Path, "uninstall.exe");
        File.WriteAllBytes(target, new byte[] { 7 });

        // Target IS the "running image" — cannot delete a live image; schedule it.
        var outcome = SelfDelete.Remove(target, runningImagePath: target);

        outcome.Should().Be(SelfDeleteOutcome.ScheduledForReboot);
        // The file must still exist right now (deletion is deferred, not immediate),
        // proving journal replay tolerates the uninstaller's own entry.
        File.Exists(target).Should().BeTrue();
    }

    [Fact]
    public void SelfDelete_on_a_missing_file_is_a_no_op()
    {
        using var dir = new TempDir();
        var target = Path.Combine(dir.Path, "gone.exe");

        SelfDelete.Remove(target, runningImagePath: null).Should().Be(SelfDeleteOutcome.NotPresent);
    }

    [Fact]
    public void RemoveUninstaller_record_round_trips_through_serialization()
    {
        using var installDir = new TempDir();
        var appId = "sigil.t15.rt." + Guid.NewGuid().ToString("N");
        var dest = Path.Combine(installDir.Path, "uninstall.exe");
        try
        {
            var journal = new RollbackJournal();
            journal.Append(new RollbackRecord.RemoveUninstaller(dest));

            UninstallStateStore.Save(appId, journal, InstallScope.User);
            var loaded = UninstallStateStore.TryLoad(appId, InstallScope.User);

            loaded.Should().NotBeNull();
            loaded!.Journal.Records.Should().ContainSingle()
                .Which.Should().BeOfType<RollbackRecord.RemoveUninstaller>()
                .Which.Path.Should().Be(dest);
        }
        finally
        {
#pragma warning disable CA1031 // Test cleanup must never mask the original assertion failure.
            try { UninstallStateStore.Delete(appId, InstallScope.User); } catch { /* best-effort */ }
#pragma warning restore CA1031
        }
    }

    [Fact]
    public async Task Uninstall_engine_reports_reversal_progress_lines()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var src = new TempDir();
        using var dst = new TempDir();
        File.WriteAllText(Path.Combine(src.Path, "x.txt"), "1");

        var appId = "sigil.t15.prog." + Guid.NewGuid().ToString("N");
        try
        {
            var steps = new InstallStep[]
            {
                new InstallStep.FileCopy(
                    "s1", From: Path.Combine(src.Path, "*.txt"), To: dst.Path,
                    Overwrite: true, When: null, OnFailure: OnFailure.Fail),
            };
            var installResult = await new InstallEngine().RunAsync(steps, StepContext.Empty);
            installResult.Success.Should().BeTrue();
            UninstallStateStore.Save(
                appId, installResult.Journal, InstallScope.User,
                secretValues: null, progress: null, installDir: dst.Path);

            var lines = new System.Collections.Generic.List<string>();
            var progress = new SyncProgress(p => { if (p.Message is not null) lines.Add(p.Message); });

            var result = await new UninstallEngine().RunAsync(appId, dst.Path, SignedDeclarations.None, InstallScope.User, progress);
            result.Success.Should().BeTrue();

            // The reversal log carries a delete line for the copied file.
            lines.Should().Contain(l => l.StartsWith("delete ", StringComparison.Ordinal));
        }
        finally
        {
#pragma warning disable CA1031 // Test cleanup must never mask the original assertion failure.
            try { UninstallStateStore.Delete(appId, InstallScope.User); } catch { /* best-effort */ }
#pragma warning restore CA1031
        }
    }

    private sealed class SyncProgress : IProgress<StepProgress>
    {
        private readonly Action<StepProgress> _sink;
        public SyncProgress(Action<StepProgress> sink) => _sink = sink;
        public void Report(StepProgress value) => _sink(value);
    }
}
