namespace SigilBuild.Wrapper.Tests.Engine;

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Cli;
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
                .RunAsync(appId, somewhereElse.Path, SignedDeclarations.None, InstallScope.User);

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
        // Arrange — an upgrade from a build that never wrote the field, with no ARP row
        // either. The caller's directory is the only anchor left, and it must be used
        // rather than refusing everything.
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
                .RunAsync(appId, installDir.Path, SignedDeclarations.None, InstallScope.User);

            // Assert
            result.Success.Should().BeTrue();
            File.Exists(installed).Should().BeFalse();
        }
        finally
        {
            UninstallStateStore.Delete(appId, InstallScope.User);
        }
    }

    /// <summary>
    /// The installed base, uninstalled through the documented <c>setup.exe /Uninstall</c>
    /// flow (<c>docs/guides/uninstaller.md</c>). Its state predates the recorded
    /// install-dir field, and the directory the current run resolves is a DEFAULT that is
    /// wrong for any install that used <c>/D=</c> or a wizard-chosen destination — the ARP
    /// <c>UninstallString</c> carries no <c>/D=</c>. The ARP <c>InstallLocation</c>, which
    /// every install has written since P3, is the one place the real directory survives.
    /// </summary>
    [WindowsFact("Windows ARP registry")]
    public async Task Pre_fix_state_recovers_the_install_dir_from_the_ARP_InstallLocation()
    {
        // Arrange — the app is installed in `real`; the uninstall run resolves
        // `wrongDefault`, standing in for the downloads folder the user ran setup.exe
        // from, or for a manifest default that never matched this install.
        using var real = new TempDir();
        using var wrongDefault = new TempDir();

        var appId = "sigil.anchor." + Guid.NewGuid().ToString("N");
        var installed = Path.Combine(real.Path, "app.exe");
        File.WriteAllText(installed, "payload");

        try
        {
            var journal = new RollbackJournal();
            journal.Append(new RollbackRecord.RestoreFile(
                installed, ExistedBefore: false, BackupPath: null));

            // Pre-fix state: no recorded install dir.
            UninstallStateStore.Save(appId, journal, InstallScope.User);
            UninstallStateStore.TryLoad(appId, InstallScope.User)!.InstallDir.Should().BeNull();

            // …but the install did write an ARP row naming where it went. HKCU, because
            // this is a user-scope fixture: the app's own uniquely-named key, removed in
            // the finally (and by UninstallEngine itself on success).
            ArpRegistration.Register(
                new ArpRegistration.Entry(
                    AppId: appId,
                    DisplayName: "Anchor Fixture",
                    DisplayVersion: "1.0.0",
                    Publisher: "Sigil Tests",
                    UninstallString: "\"" + Path.Combine(real.Path, "uninstall.exe") + "\"",
                    EstimatedSizeBytes: 0,
                    InstallLocation: real.Path),
                InstallScope.User);

            // Act
            var result = await new UninstallEngine()
                .RunAsync(appId, wrongDefault.Path, SignedDeclarations.None, InstallScope.User);

            // Assert
            result.Success.Should().BeTrue();
            File.Exists(installed).Should().BeFalse(
                "with no recorded install dir the anchor must come from the ARP " +
                "InstallLocation; anchoring to the directory this run happened to resolve " +
                "refuses every file record and leaves the installed base unremovable");
        }
        finally
        {
            UninstallStateStore.Delete(appId, InstallScope.User);
            ArpRegistration.Remove(appId, InstallScope.User);
        }
    }

    [WindowsFact("Windows ARP registry")]
    public async Task An_ARP_InstallLocation_that_would_make_the_anchor_vacuous_is_rejected()
    {
        // Arrange — the ARP value gets the same sanity floor as the recorded one. For a
        // user-scope install HKCU is the user's own hive, so this value is no more
        // trustworthy than the state file and must not be able to disarm the anchor.
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

            UninstallStateStore.Save(appId, journal, InstallScope.User);

            ArpRegistration.Register(
                new ArpRegistration.Entry(
                    AppId: appId,
                    DisplayName: "Anchor Fixture",
                    DisplayVersion: "1.0.0",
                    Publisher: "Sigil Tests",
                    UninstallString: "\"uninstall.exe\"",
                    EstimatedSizeBytes: 0,
                    InstallLocation: Path.GetPathRoot(
                        Environment.GetFolderPath(Environment.SpecialFolder.System))!),
                InstallScope.User);

            // Act
            var result = await new UninstallEngine()
                .RunAsync(appId, installDir.Path, SignedDeclarations.None, InstallScope.User);

            // Assert
            result.Success.Should().BeTrue();
            File.Exists(outside).Should().BeTrue(
                "a volume root in ARP must not be accepted as an anchor either");
        }
        finally
        {
            UninstallStateStore.Delete(appId, InstallScope.User);
            ArpRegistration.Remove(appId, InstallScope.User);
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
                .RunAsync(appId, installDir.Path, SignedDeclarations.None, InstallScope.User, progress);

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
        var act = async () => await new UninstallEngine().RunAsync("sigil.x", "   ", SignedDeclarations.None);
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
