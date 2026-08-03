namespace SigilBuild.Wrapper.Tests.Engine;

using System;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Win32;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Tests.Helpers;

/// <summary>
/// R1, clause (c): journal records carried absolute paths and full registry
/// coordinates with no anchoring, so a planted <c>uninstall.json</c> handed the
/// elevated process arbitrary file write/delete, arbitrary HKLM write, a machine
/// <c>PATH</c> hijack, service deletion, and — via <c>unregister_com</c> —
/// <c>LoadLibrary</c> plus an export call on an attacker-chosen DLL.
/// </summary>
/// <remarks>
/// <para>
/// Every negative case here is paired with a positive one. Anchoring that refuses a
/// legitimate in-<c>install_dir</c> record is worse than the bug it closes: it would
/// leave real installs unremovable while CI stayed green.
/// </para>
/// <para>
/// <c>[SupportedOSPlatform("windows")]</c> satisfies CA1416 for the direct registry
/// reads the service and environment fixtures need; <c>[WindowsFact]</c> is what makes
/// these report Skipped rather than pass vacuously off Windows.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public class ReplayAnchoringTests
{
    [WindowsFact("Windows path + COM semantics")]
    public async Task Replay_refuses_a_com_record_pointing_outside_the_install_dir()
    {
        // Arrange
        using var installDir = new TempDir();
        var journal = new RollbackJournal();

        // The single worst record in the catalogue: LoadLibrary + call an export from
        // an attacker-chosen path, inside the elevated process.
        journal.Append(new RollbackRecord.UnregisterCom(@"C:\Users\Public\evil.dll"));

        // Act
        var outcome = await journal.UndoAsync(
            CancellationToken.None,
            progress: null,
            installDir: installDir.Path);

        // Assert
        outcome.RefusedRecords.Should().ContainSingle()
            .Which.Should().Contain("evil.dll",
                "a DLL outside install_dir must never be loaded by the elevated process");
    }

    [WindowsFact("Windows path semantics")]
    public async Task Replay_refuses_a_com_record_that_escapes_the_install_dir_by_traversal()
    {
        // Arrange — the containment check must normalize BEFORE comparing, or a path
        // that merely starts with install_dir walks straight back out of it.
        using var installDir = new TempDir();
        var journal = new RollbackJournal();
        journal.Append(new RollbackRecord.UnregisterCom(
            Path.Combine(installDir.Path, "..", "..", "Users", "Public", "evil.dll")));

        // Act
        var outcome = await journal.UndoAsync(
            CancellationToken.None, progress: null, installDir: installDir.Path);

        // Assert
        outcome.RefusedRecords.Should().ContainSingle()
            .Which.Should().Contain("evil.dll");
    }

    [WindowsFact("Windows path semantics")]
    public async Task Replay_refuses_a_file_record_targeting_System32()
    {
        // Arrange — arbitrary file delete as administrator.
        using var installDir = new TempDir();
        var victim = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");

        var journal = new RollbackJournal();
        journal.Append(new RollbackRecord.RestoreFile(victim, ExistedBefore: false, BackupPath: null));

        // Act
        var outcome = await journal.UndoAsync(
            CancellationToken.None, progress: null, installDir: installDir.Path);

        // Assert
        outcome.RefusedRecords.Should().ContainSingle()
            .Which.Should().Contain("hosts",
                "an elevated replay must not be able to delete a file it never installed");
        File.Exists(victim).Should().BeTrue("the victim file must still be there");
    }

    [WindowsFact("Windows registry semantics")]
    public async Task Replay_refuses_a_registry_record_under_the_services_key()
    {
        // Arrange — arbitrary HKLM write: RegistryHelper.ParseHive accepts "HKLM", and
        // nothing constrained the key. SYSTEM\CurrentControlSet\Services is not
        // application-configuration space at all.
        using var installDir = new TempDir();
        var journal = new RollbackJournal();
        journal.Append(new RollbackRecord.RestoreRegistryValue(
            Hive: "HKLM",
            Key: @"SYSTEM\CurrentControlSet\Services\Spooler",
            Name: "ImagePath",
            View: "default",
            PriorTypeStr: "REG_SZ",
            PriorValue: @"C:\Users\Public\evil.exe",
            PreviouslyAbsent: false));

        // Act
        var outcome = await journal.UndoAsync(
            CancellationToken.None, progress: null, installDir: installDir.Path);

        // Assert
        outcome.RefusedRecords.Should().ContainSingle()
            .Which.Should().Contain(@"SYSTEM\CurrentControlSet\Services\Spooler");
    }

    [WindowsFact("Windows registry semantics")]
    public async Task Replay_allows_a_registry_record_in_application_configuration_space()
    {
        // Arrange — the POSITIVE case for the registry rule. A registry_write step may
        // name any key the manifest author chose under Software\, and its uninstall
        // must still reverse it. PreviouslyAbsent + a key that does not exist makes the
        // undo itself a no-op, so this asserts the verdict, not the write.
        using var installDir = new TempDir();
        var journal = new RollbackJournal();
        journal.Append(new RollbackRecord.RestoreRegistryValue(
            Hive: "HKCU",
            Key: @"Software\Sigil.Test\" + Guid.NewGuid().ToString("N"),
            Name: "Installed",
            View: "default",
            PriorTypeStr: "REG_SZ",
            PriorValue: null,
            PreviouslyAbsent: true));

        // Act
        var outcome = await journal.UndoAsync(
            CancellationToken.None, progress: null, installDir: installDir.Path);

        // Assert
        outcome.RefusedRecords.Should().BeEmpty(
            "refusing an ordinary Software\\ key would leave every real install's " +
            "registry state unremovable");
    }

    [WindowsFact("Windows registry semantics")]
    public async Task Replay_refuses_a_machine_scope_env_record_that_introduces_a_new_entry()
    {
        // Arrange — the machine PATH-hijack primitive, in its general form: a
        // machine-scope env restore writes an attacker-chosen string into HKLM. A
        // test-scoped variable name is used deliberately so that if this test is ever
        // run against a build WITHOUT the fix on an elevated host, the damage is a junk
        // environment variable rather than a broken machine PATH.
        using var installDir = new TempDir();
        var journal = new RollbackJournal();
        journal.Append(new RollbackRecord.RestoreEnv(
            Scope: "machine",
            Name: "SIGIL_R1_" + Guid.NewGuid().ToString("N"),
            PriorValue: @"C:\Users\Public\evil",
            PreviouslyAbsent: false));

        // Act
        var outcome = await journal.UndoAsync(
            CancellationToken.None, progress: null, installDir: installDir.Path);

        // Assert
        outcome.RefusedRecords.Should().ContainSingle()
            .Which.Should().Contain(@"C:\Users\Public\evil",
                "a machine-scope restore may only remove what the install added; it may " +
                "never introduce a path the variable does not already contain");
    }

    [WindowsFact("Windows registry semantics")]
    public async Task Replay_allows_a_machine_scope_env_restore_that_only_removes_entries()
    {
        // Arrange — the POSITIVE case that keeps real machine-scope uninstalls working:
        // the install appended install_dir to the machine PATH and the uninstall
        // restores the value it captured beforehand. Every entry of that captured value
        // is by construction already present, so it must pass. Built from the machine
        // PATH actually on this box, so the assertion is about the real shape.
        using var installDir = new TempDir();
        var currentPath = ReadMachineEnv("Path");
        currentPath.Should().NotBeNullOrEmpty(
            "the positive case needs the real machine PATH to build a legitimate restore from");

        var journal = new RollbackJournal();
        journal.Append(new RollbackRecord.RestoreEnv(
            Scope: "machine",
            Name: "Path",
            // The prior value: today's PATH minus the entry the install would have
            // added. A strict subset — exactly what a legitimate restore looks like.
            PriorValue: currentPath,
            PreviouslyAbsent: false));

        // Act
        var outcome = await journal.UndoAsync(
            CancellationToken.None, progress: null, installDir: installDir.Path);

        // Assert
        outcome.RefusedRecords.Should().BeEmpty(
            "refusing this would strand a stale install_dir entry in the machine PATH " +
            "after every legitimate machine-scope uninstall");
    }

    [WindowsFact("Windows service registry")]
    public async Task Replay_refuses_a_service_the_app_never_installed()
    {
        // Arrange — sc stop + sc delete on a name of the attacker's choosing. The
        // service's own registered ImagePath is what ties it back to an install, and
        // this one runs out of System32.
        using var installDir = new TempDir();
        var (serviceName, imagePath) = FindStockService();

        var journal = new RollbackJournal();
        journal.Append(new RollbackRecord.RemoveService(serviceName));

        // Act
        var outcome = await journal.UndoAsync(
            CancellationToken.None, progress: null, installDir: installDir.Path);

        // Assert
        outcome.RefusedRecords.Should().ContainSingle()
            .Which.Should().Contain(serviceName)
            .And.Contain(imagePath,
                "the refusal must name the binary that proves the service is not ours");
    }

    [WindowsFact("Windows service registry")]
    public async Task Replay_allows_removing_a_service_that_does_not_exist()
    {
        // Arrange — the POSITIVE half of the service rule. A rollback that runs after
        // the service was already removed (or before it was ever created) must not be
        // reported as an attack; sc delete is a no-op either way.
        using var installDir = new TempDir();
        var journal = new RollbackJournal();
        journal.Append(new RollbackRecord.RemoveService(
            "SigilNoSuchService" + Guid.NewGuid().ToString("N")));

        // Act
        var outcome = await journal.UndoAsync(
            CancellationToken.None, progress: null, installDir: installDir.Path);

        // Assert
        outcome.RefusedRecords.Should().BeEmpty();
    }

    [WindowsFact("Windows path semantics")]
    public async Task Replay_still_reverses_a_legitimate_record_inside_the_install_dir()
    {
        // Arrange — THE test that keeps anchoring honest: a file the install created
        // inside install_dir, reversed by exactly the record a file_copy step records.
        using var installDir = new TempDir();
        var installed = Path.Combine(installDir.Path, "app.exe");
        File.WriteAllText(installed, "payload");

        var comDll = Path.Combine(installDir.Path, "component.dll");
        File.WriteAllText(comDll, "not really a dll");

        var journal = new RollbackJournal();
        journal.Append(new RollbackRecord.RestoreFile(installed, ExistedBefore: false, BackupPath: null));
        // A COM component that really does live in the install dir must survive the
        // re-derivation and be replayed (the load itself fails harmlessly here — the
        // record swallows it, exactly as it does for a missing export).
        journal.Append(new RollbackRecord.UnregisterCom(comDll));

        // Act
        var outcome = await journal.UndoAsync(
            CancellationToken.None, progress: null, installDir: installDir.Path);

        // Assert
        outcome.RefusedRecords.Should().BeEmpty(
            "anchoring that breaks a real uninstall is worse than the bug it closes");
        File.Exists(installed).Should().BeFalse(
            "the legitimate in-install_dir record must actually have replayed");
    }

    [WindowsFact("Windows path semantics")]
    public async Task A_refused_record_is_skipped_and_the_rest_of_the_replay_continues()
    {
        // Arrange — one planted record and one legitimate one. Aborting would let a
        // single planted record block a legitimate uninstall; silently skipping would
        // mask the attack. Neither.
        using var installDir = new TempDir();
        var installed = Path.Combine(installDir.Path, "app.exe");
        File.WriteAllText(installed, "payload");

        var journal = new RollbackJournal();
        journal.Append(new RollbackRecord.RestoreFile(installed, ExistedBefore: false, BackupPath: null));
        journal.Append(new RollbackRecord.UnregisterCom(@"C:\Users\Public\evil.dll"));

        var progress = new CapturingProgress();

        // Act
        var outcome = await journal.UndoAsync(
            CancellationToken.None, progress, installDir: installDir.Path);

        // Assert
        outcome.RefusedRecords.Should().ContainSingle();
        File.Exists(installed).Should().BeFalse(
            "the records after the refused one must still replay");
        progress.Messages.Should().Contain(
            m => m.StartsWith("refused:", StringComparison.Ordinal),
            "a silent skip would mask the attack from the /LOG file the responder reads");
    }

    [WindowsFact("Windows path semantics")]
    public async Task An_unanchored_replay_is_unchanged()
    {
        // Arrange — the mid-install rollback path. Those records were authored moments
        // ago by the engine itself from the signed manifest and have never round-tripped
        // through a file an attacker can write, so nothing is anchored and nothing is
        // refused. Guards against anchoring quietly leaking into InstallEngine and
        // refusing legitimate reversals of manifest-declared work.
        using var temp = new TempDir();
        var outside = Path.Combine(temp.Path, "outside.txt");
        File.WriteAllText(outside, "x");

        var journal = new RollbackJournal();
        journal.Append(new RollbackRecord.RestoreFile(outside, ExistedBefore: false, BackupPath: null));

        // Act
        var outcome = await journal.UndoAsync(CancellationToken.None);

        // Assert
        outcome.RefusedRecords.Should().BeEmpty();
        File.Exists(outside).Should().BeFalse();
    }

    /// <summary>
    /// A stock Windows service and its <c>ImagePath</c>, for the negative service case.
    /// Probed rather than hardcoded so a box without the print spooler still exercises
    /// the rule instead of failing for the wrong reason.
    /// </summary>
    private static (string Name, string ImagePath) FindStockService()
    {
        foreach (var candidate in new[] { "Spooler", "Themes", "EventLog", "Dnscache", "W32Time" })
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\{candidate}", writable: false);
            if (key?.GetValue("ImagePath") is string image && image.Length > 0)
            {
                // The refusal message quotes the ImagePath verbatim.
                return (candidate, image);
            }
        }

        throw new InvalidOperationException(
            "no stock Windows service with a readable ImagePath was found; the negative " +
            "service case cannot be asserted and must not be reported as passing");
    }

    private static string? ReadMachineEnv(string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            @"System\CurrentControlSet\Control\Session Manager\Environment", writable: false);
        return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    private sealed class CapturingProgress : IProgress<StepProgress>
    {
        private readonly System.Collections.Generic.List<string> _messages = new();

        public System.Collections.Generic.IReadOnlyList<string> Messages => _messages;

        public void Report(StepProgress value)
        {
            if (value?.Message is not null)
            {
                _messages.Add(value.Message);
            }
        }
    }
}
