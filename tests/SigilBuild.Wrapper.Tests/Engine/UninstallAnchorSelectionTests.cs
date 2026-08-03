namespace SigilBuild.Wrapper.Tests.Engine;

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Tests.Helpers;

/// <summary>
/// R1 clause (c), the half that decides whether the fix is a fix or an outage: WHICH
/// directory the replay is anchored to.
/// </summary>
/// <remarks>
/// The ARP <c>UninstallString</c> carries no <c>/D=</c>, so an uninstall cannot
/// recompute where a wizard-chosen or <c>/D=</c> install actually went — it would
/// resolve the DEFAULT destination instead. Anchoring to that default refuses every
/// file record of such an install, and then the ARP row and the state are deleted
/// anyway: the app is left on disk and unremovable, silently. The install directory is
/// therefore recorded at save time and preferred at load time.
/// </remarks>
[SupportedOSPlatform("windows")]
public class UninstallAnchorSelectionTests
{
    [WindowsFact("Windows-only state layout")]
    public async Task An_install_into_a_non_default_directory_is_still_fully_uninstalled()
    {
        // Arrange — an install that landed somewhere the uninstall could never guess,
        // exactly as /D= or a wizard-chosen destination does.
        using var chosen = new TempDir();
        using var somewhereElse = new TempDir();

        var appId = "sigil.anchor." + Guid.NewGuid().ToString("N");
        var installed = Path.Combine(chosen.Path, "app.exe");
        File.WriteAllText(installed, "payload");

        try
        {
            var journal = new RollbackJournal();
            journal.Append(new RollbackRecord.RestoreFile(
                installed, ExistedBefore: false, BackupPath: null));

            // The install records where it really went.
            UninstallStateStore.Save(
                appId, journal, InstallScope.User, secretValues: null, progress: null,
                installDir: chosen.Path);

            UninstallStateStore.TryLoad(appId, InstallScope.User)!.InstallDir
                .Should().Be(chosen.Path, "the fixture depends on the dir being recorded");

            // Act — the uninstall resolves a DIFFERENT directory for this run (it has no
            // /D= to work from) and passes it as the fallback.
            var result = await new UninstallEngine()
                .RunAsync(appId, somewhereElse.Path, InstallScope.User);

            // Assert
            result.Success.Should().BeTrue();
            File.Exists(installed).Should().BeFalse(
                "the replay must anchor to the RECORDED install directory; anchoring to a " +
                "recomputed default refuses every file record and leaves the app installed " +
                "but unremovable");
        }
        finally
        {
            UninstallStateStore.Delete(appId, InstallScope.User);
        }
    }

    [WindowsFact("Windows-only state layout")]
    public async Task State_written_before_the_recorded_directory_existed_falls_back_to_the_caller()
    {
        // Arrange — an upgrade from a build that never wrote the field. The fallback is
        // the only anchor available, and it must be used rather than refusing everything.
        using var installDir = new TempDir();
        var appId = "sigil.anchor." + Guid.NewGuid().ToString("N");
        var installed = Path.Combine(installDir.Path, "app.exe");
        File.WriteAllText(installed, "payload");

        try
        {
            var journal = new RollbackJournal();
            journal.Append(new RollbackRecord.RestoreFile(
                installed, ExistedBefore: false, BackupPath: null));

            // installDir deliberately omitted — the pre-fix wire shape.
            UninstallStateStore.Save(appId, journal, InstallScope.User);
            UninstallStateStore.TryLoad(appId, InstallScope.User)!.InstallDir.Should().BeNull();

            // Act
            var result = await new UninstallEngine()
                .RunAsync(appId, installDir.Path, InstallScope.User);

            // Assert
            result.Success.Should().BeTrue();
            File.Exists(installed).Should().BeFalse();
        }
        finally
        {
            UninstallStateStore.Delete(appId, InstallScope.User);
        }
    }

    [WindowsFact("Windows-only state layout")]
    public async Task A_recorded_directory_that_would_make_the_anchor_vacuous_is_rejected()
    {
        // Arrange — the flip side of preferring the recorded value: it comes out of the
        // state file. For machine scope the file has already passed S1.1's provenance
        // gate, but the sanity floor is what stops a value chosen purely to disarm the
        // anchor — a volume root, %WINDIR%, %ProgramFiles% itself. Recording C:\ would
        // otherwise let every record on the volume through.
        using var installDir = new TempDir();
        using var elsewhere = new TempDir();
        var appId = "sigil.anchor." + Guid.NewGuid().ToString("N");
        var outside = Path.Combine(elsewhere.Path, "victim.txt");
        File.WriteAllText(outside, "payload");

        try
        {
            var journal = new RollbackJournal();
            journal.Append(new RollbackRecord.RestoreFile(
                outside, ExistedBefore: false, BackupPath: null));

            UninstallStateStore.Save(
                appId, journal, InstallScope.User, secretValues: null, progress: null,
                installDir: Path.GetPathRoot(Environment.GetFolderPath(
                    Environment.SpecialFolder.System)));

            var progress = new CapturingProgress();

            // Act
            var result = await new UninstallEngine()
                .RunAsync(appId, installDir.Path, InstallScope.User, progress);

            // Assert
            result.Success.Should().BeTrue("a rejected anchor must not abort the uninstall");
            File.Exists(outside).Should().BeTrue(
                "the volume root must not be accepted as an anchor, or anchoring means nothing");
            progress.Messages.Should().Contain(
                m => m.Contains("not a directory any install could have used", StringComparison.Ordinal),
                "substituting the anchor silently would hide the tampering");
        }
        finally
        {
            UninstallStateStore.Delete(appId, InstallScope.User);
#pragma warning disable CA1031 // Best-effort cleanup of the test's own scratch file.
            try
            {
                if (File.Exists(outside))
                {
                    File.Delete(outside);
                }
            }
            catch
            {
                // Best-effort.
            }
#pragma warning restore CA1031
        }
    }

    [WindowsFact("Windows-only state layout")]
    public async Task The_uninstall_entry_point_cannot_be_called_without_an_anchor()
    {
        // The only entry point that replays persisted state must not be callable in a
        // way that silently loses anchoring. Omitting the argument is a compile error;
        // this covers the remaining runtime hole of passing a blank one.
        var act = async () => await new UninstallEngine().RunAsync("sigil.x", "   ");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    private sealed class CapturingProgress : IProgress<StepProgress>
    {
        private readonly List<string> _messages = new();

        public IReadOnlyList<string> Messages => _messages;

        public void Report(StepProgress value)
        {
            if (value?.Message is not null)
            {
                _messages.Add(value.Message);
            }
        }
    }
}
