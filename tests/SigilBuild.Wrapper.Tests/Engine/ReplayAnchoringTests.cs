namespace SigilBuild.Wrapper.Tests.Engine;

using System;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Win32;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Tests.Helpers;
using Xunit;

/// <summary>
/// R1, clause (c): journal records carried absolute paths and full registry
/// coordinates with no anchoring, so a planted <c>uninstall.json</c> handed the
/// elevated process arbitrary file write/delete, arbitrary HKLM write, a machine
/// <c>PATH</c> hijack, service deletion, and — via <c>unregister_com</c> —
/// <c>LoadLibrary</c> plus an export call on an attacker-chosen DLL.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Nothing in this file can damage the host, elevated or not.</strong> The rule
/// is mechanical and applies to every test here:
/// </para>
/// <list type="bullet">
///   <item>
///     A record naming a REAL machine coordinate — the machine <c>PATH</c>, a live
///     service, <c>System32</c>, <c>HKLM\SYSTEM</c> — is only ever handed to
///     <c>ReplayAnchor.Check</c>, which is a pure predicate. It computes a verdict and
///     returns; it never invokes <c>RollbackRecord.UndoAsync</c>, so no registry write,
///     no file delete and no <c>sc delete</c> can occur — whatever the verdict is, and
///     however the predicate might regress.
///   </item>
///   <item>
///     A record handed to <see cref="RollbackJournal.UndoAsync"/> — which does execute
///     — names only scratch coordinates this test created: files and directories under
///     a <see cref="TempDir"/>, or a uniquely-named <c>.lnk</c> the test writes into the
///     current user's Start Menu and removes in a <c>finally</c>. If anchoring
///     regressed to "allow everything", the worst outcome is that a temp file the test
///     itself created is deleted.
///   </item>
/// </list>
/// <para>
/// Every negative case is paired with a positive one. Anchoring that refuses a
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
    // ---------------------------------------------------------------------------
    // COM — the worst record in the catalogue.
    // ---------------------------------------------------------------------------

    [WindowsFact("Windows path + COM semantics")]
    public async Task Replay_refuses_a_com_record_pointing_outside_the_install_dir()
    {
        // Arrange — LoadLibrary + call an export from an attacker-chosen path, inside
        // the elevated process. The path is a scratch one: this test DOES execute the
        // replay, to prove the refusal is wired through UndoAsync and not merely
        // computable by the predicate.
        using var installDir = new TempDir();
        using var elsewhere = new TempDir();
        var evil = Path.Combine(elsewhere.Path, "evil.dll");

        var journal = new RollbackJournal();
        journal.Append(new RollbackRecord.UnregisterCom(evil));

        // Act
        var outcome = await journal.UndoAsync(
            ReplayAnchorage.ForInstallDir(installDir.Path), progress: null, ct: CancellationToken.None);

        // Assert
        outcome.RefusedRecords.Should().ContainSingle()
            .Which.Should().Contain("evil.dll",
                "a DLL outside install_dir must never be loaded by the elevated process");
    }

    [WindowsFact("Windows path semantics")]
    public void Replay_refuses_a_com_record_that_escapes_the_install_dir_by_traversal()
    {
        // Arrange — the containment check must normalize BEFORE comparing, or a path
        // that merely starts with install_dir walks straight back out of it.
        using var installDir = new TempDir();
        var record = new RollbackRecord.UnregisterCom(
            Path.Combine(installDir.Path, "..", "..", "Users", "Public", "evil.dll"));

        // Act
        var verdict = Anchor(installDir.Path).Check(record);

        // Assert
        verdict.RefusalReason.Should().NotBeNull();
        verdict.RefusalReason!.Should().Contain("evil.dll");
    }

    [WindowsFact("Windows path semantics")]
    public void A_com_record_inside_the_install_dir_is_re_derived_not_merely_accepted()
    {
        // Arrange — the POSITIVE case. The replayed path must be rebuilt from
        // install_dir rather than taken verbatim from the file.
        using var installDir = new TempDir();
        var recorded = Path.Combine(installDir.Path, "sub", "..", "component.dll");

        // Act
        var verdict = Anchor(installDir.Path).Check(new RollbackRecord.UnregisterCom(recorded));

        // Assert
        verdict.RefusalReason.Should().BeNull();
        verdict.Record.Should().BeOfType<RollbackRecord.UnregisterCom>()
            .Which.DllPath.Should().Be(Path.Combine(installDir.Path, "component.dll"),
                "the DLL path must be re-derived from install_dir, not trusted as persisted");
    }

    // ---------------------------------------------------------------------------
    // Filesystem records — one refusal and one legitimate replay for each.
    // ---------------------------------------------------------------------------

    [WindowsFact("Windows path semantics")]
    public void Replay_refuses_a_file_record_targeting_System32()
    {
        // Arrange — arbitrary file delete as administrator. Predicate only: the record
        // is never replayed, so the real hosts file cannot be touched by this test on
        // any host, however the predicate behaves.
        using var installDir = new TempDir();
        var victim = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");

        // Act
        var verdict = Anchor(installDir.Path).Check(
            new RollbackRecord.RestoreFile(victim, ExistedBefore: false, BackupPath: null));

        // Assert
        verdict.RefusalReason.Should().NotBeNull();
        verdict.RefusalReason!.Should().Contain("hosts",
            "an elevated replay must not be able to delete a file it never installed");
    }

    [WindowsTheory("Windows path semantics")]
    [InlineData("restore_file")]
    [InlineData("remove_directory")]
    [InlineData("delete_shortcut")]
    [InlineData("remove_uninstaller")]
    [InlineData("restore_deleted_file")]
    [InlineData("restore_deleted_directory")]
    [InlineData("restore_config_file")]
    public void Every_path_bearing_record_is_refused_outside_the_anchor(string type)
    {
        // Arrange — the whole path-bearing half of R1's evidence table, each with a
        // target in a scratch directory that is neither the install dir nor a scope
        // root. Predicate only; nothing is executed.
        using var installDir = new TempDir();
        using var elsewhere = new TempDir();
        var outside = Path.Combine(elsewhere.Path, "outside.dat");

        // Act
        var verdict = Anchor(installDir.Path).Check(PathRecord(type, outside));

        // Assert
        verdict.RefusalReason.Should().NotBeNull();
        verdict.RefusalReason!.Should().StartWith(type + " refused:").And.Contain("outside.dat");
    }

    [WindowsTheory("Windows path semantics")]
    [InlineData("restore_file")]
    [InlineData("remove_directory")]
    [InlineData("delete_shortcut")]
    [InlineData("remove_uninstaller")]
    [InlineData("restore_deleted_file")]
    [InlineData("restore_deleted_directory")]
    [InlineData("restore_config_file")]
    public void Every_path_bearing_record_is_allowed_inside_the_install_dir(string type)
    {
        // Arrange — the matching positive for each. Without these the whole suite would
        // be satisfied by a predicate that refuses everything, which would leave every
        // real install unremovable.
        using var installDir = new TempDir();
        var inside = Path.Combine(installDir.Path, "sub", "file.dat");

        // Act
        var verdict = Anchor(installDir.Path).Check(PathRecord(type, inside));

        // Assert
        verdict.RefusalReason.Should().BeNull(
            "a record targeting the install directory is exactly what a legitimate " +
            "uninstall replays");
    }

    [WindowsFact("Windows shell folders")]
    public async Task A_shortcut_in_the_start_menu_is_replayed_although_it_is_outside_the_install_dir()
    {
        // Arrange — the main legitimate out-of-install_dir case: shortcut_create writes
        // into the Start Menu and its reversal must still run. Executes for real, but
        // only against a uniquely-named file this test creates in the CURRENT USER's
        // Start Menu and removes in the finally.
        using var installDir = new TempDir();
        var startMenu = ScopeLayout.For(InstallScope.User).StartMenuFolder;
        Directory.Exists(startMenu).Should().BeTrue(
            "the positive case needs the real per-user Start Menu folder to exist");

        var lnk = Path.Combine(startMenu, $"sigil-anchor-test-{Guid.NewGuid():N}.lnk");
        try
        {
            File.WriteAllText(lnk, "not really a shortcut");

            var journal = new RollbackJournal();
            journal.Append(new RollbackRecord.DeleteShortcut(lnk));

            // Act
            var outcome = await journal.UndoAsync(
                ReplayAnchorage.ForInstallDir(installDir.Path), progress: null, ct: CancellationToken.None);

            // Assert
            outcome.RefusedRecords.Should().BeEmpty(
                "refusing this would strand a Start Menu shortcut after every uninstall");
            File.Exists(lnk).Should().BeFalse("the shortcut removal must actually have run");
        }
        finally
        {
#pragma warning disable CA1031 // Best-effort cleanup of the test's own scratch shortcut.
            try
            {
                if (File.Exists(lnk))
                {
                    File.Delete(lnk);
                }
            }
            catch
            {
                // Best-effort.
            }
#pragma warning restore CA1031
        }
    }

    // ---------------------------------------------------------------------------
    // Registry — the hive is load-bearing, and Software\ is not automatically safe.
    // ---------------------------------------------------------------------------

    [WindowsTheory("Windows registry semantics")]
    // Not application-configuration space at all: R1's named coordinates.
    [InlineData("HKLM", @"SYSTEM\CurrentControlSet\Services\Spooler")]
    [InlineData("HKLM", @"System\CurrentControlSet\Control\Session Manager\Environment")]
    // Execution hijacks that DO live under Software\ — a bare "must be under Software"
    // rule waves every one of these through.
    [InlineData("HKLM", @"Software\Classes\exefile\shell\open\command")]
    [InlineData("HKCR", @"exefile\shell\open\command")]
    [InlineData("HKLM", @"Software\Microsoft\Windows\CurrentVersion\App Paths\notepad.exe")]
    [InlineData("HKLM", @"Software\Microsoft\Windows\CurrentVersion\Run")]
    [InlineData("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Run")]
    [InlineData("HKLM", @"Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Run")]
    [InlineData("HKLM", @"Software\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\sethc.exe")]
    [InlineData("HKLM", @"Software\Classes\CLSID\{00000000-0000-0000-0000-000000000000}\InprocServer32")]
    [InlineData("HKLM", @"Software\Classes\Directory\shell\evil\command")]
    [InlineData("HKLM", @"Software\Classes\.exe\shell\open\command")]
    [InlineData("HKLM", @"Software\Policies\Microsoft\Windows\System")]
    // The progids each successive review round turned up. They are refused by SHAPE —
    // none of these names appears anywhere in ReplayAnchor — which is the point: the
    // next unheard-of progid is covered too.
    [InlineData("HKLM", @"Software\Classes\txtfile\shell\open\command")]
    [InlineData("HKLM", @"Software\Classes\lnkfile\shell\open\command")]
    [InlineData("HKLM", @"Software\Classes\mscfile\shell\open\command")]
    [InlineData("HKLM", @"Software\Classes\batfile\shell\open\command")]
    [InlineData("HKLM", @"Software\Classes\htmlfile\shell\open\command")]
    [InlineData("HKLM", @"Software\Classes\SomeProgidWindowsShipsThatWeHaveNeverHeardOf\shell\print\command")]
    [InlineData("HKLM", @"Software\Classes\Acme.Document\shell\open\ddeexec")]
    [InlineData("HKLM", @"Software\Classes\Acme.Document\shellex\ContextMenuHandlers\Evil")]
    [InlineData("HKLM", @"Software\Classes\Acme.Document\CLSID\{0}\LocalServer32")]
    // Driver mapping — the other shape that hands Windows a module to load.
    [InlineData("HKLM", @"Software\Microsoft\Windows NT\CurrentVersion\Drivers32")]
    [InlineData("HKLM", @"Software\Microsoft\Windows NT\CurrentVersion\Drivers.desc")]
    // Hives an installer's rollback has no business in.
    [InlineData("HKU", @"Software\Acme\App")]
    [InlineData("HKCC", @"Software\Acme\App")]
    public void Registry_records_outside_reversible_configuration_space_are_refused(string hive, string key)
    {
        // Arrange — predicate only; no registry key is opened, read or written.
        using var installDir = new TempDir();
        var record = new RollbackRecord.RestoreRegistryValue(
            hive, key, "Value", "default", "REG_SZ", @"C:\Users\Public\evil.exe", PreviouslyAbsent: false);

        // Act
        var verdict = Anchor(installDir.Path).Check(record);

        // Assert
        verdict.RefusalReason.Should().NotBeNull();
        verdict.RefusalReason!.Should().Contain(key);
    }

    [WindowsTheory("Windows registry semantics")]
    [InlineData("HKCU", @"Software\Acme\App")]
    [InlineData("HKLM", @"Software\Acme\App\Settings")]
    [InlineData("HKLM", @"Software\Wow6432Node\Acme\App")]
    [InlineData("HKLM", @"Software\Microsoft\Windows\CurrentVersion\Uninstall\sigil.acme.app")]
    [InlineData("HKLM", @"Software\Classes\Acme.Document\shell\open\command")]
    [InlineData("HKLM", @"Software\Classes\.acme")]
    public void Registry_records_in_ordinary_application_space_are_allowed(string hive, string key)
    {
        // Arrange — the positives that keep real uninstalls working: a registry_write
        // step may name any key the manifest author chose, including the app's own
        // progid, its own file extension, and its own ARP row.
        using var installDir = new TempDir();
        var record = new RollbackRecord.RestoreRegistryValue(
            hive, key, "Installed", "default", "REG_SZ", null, PreviouslyAbsent: true);

        // Act
        var verdict = Anchor(installDir.Path).Check(record);

        // Assert
        verdict.RefusalReason.Should().BeNull();
    }

    [WindowsFact("Windows registry semantics")]
    public void An_app_restoring_its_own_file_association_to_its_own_binary_is_allowed()
    {
        // Arrange — THE positive for the shape rule, and the reason it is not simply
        // "deny every shell\...\command". Registering a file type is what installers do;
        // the command points at the app's own exe inside install_dir, quoted with the
        // usual "%1" argument.
        using var installDir = new TempDir();
        var record = new RollbackRecord.RestoreRegistryValue(
            "HKLM",
            @"Software\Classes\Acme.Document\shell\open\command",
            Name: "",
            View: "default",
            PriorTypeStr: "REG_SZ",
            PriorValue: $"\"{Path.Combine(installDir.Path, "acme.exe")}\" \"%1\"",
            PreviouslyAbsent: false);

        // Act
        var verdict = Anchor(installDir.Path).Check(record);

        // Assert
        verdict.RefusalReason.Should().BeNull(
            "denying the shape outright would break every installer that registers a " +
            "file type — the value must be checked, not the key alone");
    }

    [WindowsFact("Windows registry semantics")]
    public void The_same_association_pointing_outside_the_install_dir_is_refused()
    {
        // Arrange — the negative twin: same key, same progid, attacker-chosen program.
        // This is what makes the rule categorical: the progid is irrelevant, the target
        // is what decides.
        using var installDir = new TempDir();
        using var elsewhere = new TempDir();
        var record = new RollbackRecord.RestoreRegistryValue(
            "HKLM",
            @"Software\Classes\Acme.Document\shell\open\command",
            Name: "",
            View: "default",
            PriorTypeStr: "REG_SZ",
            PriorValue: $"\"{Path.Combine(elsewhere.Path, "evil.exe")}\" \"%1\"",
            PreviouslyAbsent: false);

        // Act
        var verdict = Anchor(installDir.Path).Check(record);

        // Assert
        verdict.RefusalReason.Should().NotBeNull();
        verdict.RefusalReason!.Should().Contain("evil.exe").And.Contain("execution mapping");
    }

    [WindowsFact("Windows registry semantics")]
    public void RestoreRegistryKey_obeys_the_same_rule_as_RestoreRegistryValue()
    {
        // Arrange — the key-level record was never covered; it writes just as much.
        using var installDir = new TempDir();
        var anchor = Anchor(installDir.Path);
        var snapshots = Array.Empty<RegistryValueSnapshot>();

        // Act
        var refused = anchor.Check(new RollbackRecord.RestoreRegistryKey(
            "HKLM", @"SYSTEM\CurrentControlSet\Services\Spooler", "default", snapshots, false));
        var allowed = anchor.Check(new RollbackRecord.RestoreRegistryKey(
            "HKLM", @"Software\Acme\App", "default", snapshots, false));

        // Assert
        refused.RefusalReason.Should().NotBeNull();
        refused.RefusalReason!.Should().Contain("Services");
        allowed.RefusalReason.Should().BeNull();
    }

    // ---------------------------------------------------------------------------
    // Environment — R1's named PATH-hijack primitive, in both scopes.
    // ---------------------------------------------------------------------------

    [WindowsFact("Windows registry semantics")]
    public void Machine_env_restore_that_introduces_a_new_entry_is_refused()
    {
        // Arrange — predicate only; HKLM is read, never written.
        using var installDir = new TempDir();
        var record = new RollbackRecord.RestoreEnv(
            "machine", "Path", @"C:\Users\Public\evil;" + ReadMachineEnv("Path"), PreviouslyAbsent: false);

        // Act
        var verdict = Anchor(installDir.Path).Check(record);

        // Assert
        verdict.RefusalReason.Should().NotBeNull();
        verdict.RefusalReason!.Should().Contain(@"C:\Users\Public\evil",
            "a restore may only remove what the install added; it may never introduce a " +
            "path the variable does not already contain");
    }

    [WindowsFact("Windows registry semantics")]
    public void Machine_env_restore_cannot_introduce_a_user_writable_directory()
    {
        // Arrange — the hole a filesystem-shaped allowlist leaves open. The install dir
        // here is under %TEMP%, which the current user owns outright: it is inside the
        // anchor for FILE records, and must still be refused as a new machine-PATH
        // entry, because a user-writable directory on the machine PATH is the hijack.
        using var installDir = new TempDir();
        StateDirectorySecurity.IsAdminOnlyWritable(installDir.Path).Should().BeFalse(
            "the fixture must really be a user-writable directory, or this proves nothing");

        var record = new RollbackRecord.RestoreEnv(
            "machine", "Path", installDir.Path + ";" + ReadMachineEnv("Path"), PreviouslyAbsent: false);

        // Act
        var verdict = Anchor(installDir.Path).Check(record);

        // Assert
        verdict.RefusalReason.Should().NotBeNull();
        verdict.RefusalReason!.Should().Contain(installDir.Path,
            "a machine-PATH entry that a non-administrator can write is R1's hijack, " +
            "whether or not it happens to be the install directory");
    }

    [WindowsFact("Windows registry semantics")]
    public void Machine_env_restore_that_only_removes_entries_is_allowed()
    {
        // Arrange — the POSITIVE case that keeps real machine-scope uninstalls working:
        // the install appended install_dir to the machine PATH and the uninstall
        // restores the value it captured beforehand, every entry of which is by
        // construction already present. Built from the machine PATH actually on this
        // box, in its LITERAL (REG_EXPAND_SZ, unexpanded) form — the shape that first
        // exposed an expansion bug here. Predicate only; HKLM is never written.
        using var installDir = new TempDir();
        var literalPath = ReadMachineEnv("Path");
        literalPath.Should().NotBeNullOrEmpty(
            "the positive case needs the real machine PATH to build a legitimate restore from");

        var record = new RollbackRecord.RestoreEnv(
            "machine", "Path", literalPath, PreviouslyAbsent: false);

        // Act
        var verdict = Anchor(installDir.Path).Check(record);

        // Assert
        verdict.RefusalReason.Should().BeNull(
            "refusing this would strand a stale install_dir entry in the machine PATH " +
            "after every legitimate machine-scope uninstall");
    }

    [WindowsTheory("Windows registry semantics")]
    [InlineData("machine")]
    [InlineData("user")]
    public void Deleting_PATH_is_refused_in_either_scope(string scope)
    {
        // Arrange — PreviouslyAbsent means the undo DELETES the variable. Expressed as
        // "compare the current value against itself" the branch could never refuse
        // anything, and "restore PATH to absent" — which breaks the box — would be
        // permitted. Predicate only; the registry is read, never written.
        //
        // The assertion deliberately does NOT depend on the ambient profile. The user
        // row previously required HKCU\Environment\Path to exist and would have gone red
        // on a fresh profile or a clean CI runner. Rather than have the fixture write a
        // real PATH value — precisely the kind of thing this file exists to avoid — the
        // predicate now fails closed when a system-critical variable's current value
        // cannot be read, so the refusal holds on every host. Both branches produce a
        // reason beginning "deleting <scope>-scope 'Path'", which is what is asserted.
        using var installDir = new TempDir();
        var record = new RollbackRecord.RestoreEnv(
            scope, "Path", PriorValue: null, PreviouslyAbsent: true);

        // Act
        var verdict = Anchor(installDir.Path).Check(record);

        // Assert
        verdict.RefusalReason.Should().NotBeNull();
        verdict.RefusalReason!.Should().Contain($"deleting {scope}-scope 'Path'",
            "PATH's entries were not created by this install, and losing it breaks the " +
            "machine — a destructive primitive in its own right");
    }

    [WindowsFact("Windows registry semantics")]
    public void Deleting_a_system_critical_variable_is_refused_even_when_it_is_absent()
    {
        // Arrange — the fresh-profile shape, pinned so the fail-closed branch cannot be
        // removed unnoticed. `windir` is never present in HKCU\Environment on any
        // profile, so the "current value could not be read" path is the one that runs,
        // deterministically, on every host. Read-only.
        using var installDir = new TempDir();
        var record = new RollbackRecord.RestoreEnv(
            "user", "windir", PriorValue: null, PreviouslyAbsent: true);

        // Act
        var verdict = Anchor(installDir.Path).Check(record);

        // Assert
        verdict.RefusalReason.Should().NotBeNull();
        verdict.RefusalReason!.Should().Contain("could not be read",
            "with nothing to run the ownership test against, the only safe answer for a " +
            "variable the system depends on is to refuse");
    }

    [WindowsFact("Windows registry semantics")]
    public void Deleting_an_ordinary_application_variable_the_install_created_is_allowed()
    {
        // Arrange — the POSITIVE half of the delete rule, and the case that keeps real
        // uninstalls (and the T10 reinstall cleanup) working: an installer legitimately
        // sets something like ACME_HOME to a data directory OUTSIDE install_dir, and its
        // removal must still replay. A false "the install created this" claim on an
        // ordinary variable costs one deleted application variable — a nuisance, not a
        // privilege — whereas refusing it strands a variable after every uninstall.
        using var installDir = new TempDir();
        var record = new RollbackRecord.RestoreEnv(
            "user", "SIGIL_ANCHOR_" + Guid.NewGuid().ToString("N"),
            PriorValue: null, PreviouslyAbsent: true);

        // Act
        var verdict = Anchor(installDir.Path).Check(record);

        // Assert
        verdict.RefusalReason.Should().BeNull();
    }

    [WindowsFact("Windows registry semantics")]
    public void User_scope_env_restore_is_checked_too_because_HKCU_is_the_admins_hive_when_elevated()
    {
        // Arrange — "user scope is the caller's own hive" is false during an ELEVATED
        // replay: HKCU is then the administrator's, and a standard user planting the
        // record would be hijacking it. Predicate only; HKCU is read, never written.
        using var installDir = new TempDir();
        var record = new RollbackRecord.RestoreEnv(
            "user", "Path", @"C:\Users\Public\evil", PreviouslyAbsent: false);

        // Act
        var verdict = Anchor(installDir.Path).Check(record);

        // Assert
        verdict.RefusalReason.Should().NotBeNull();
        verdict.RefusalReason!.Should().Contain(@"C:\Users\Public\evil");
    }

    [WindowsFact("Windows registry semantics")]
    public void User_scope_env_restore_of_the_install_dir_is_allowed()
    {
        // Arrange — the POSITIVE for user scope. A user-scope install legitimately lands
        // in a user-writable directory (%LocalAppData%\Programs), so the admin-only
        // requirement must NOT apply here or every per-user PATH uninstall would break.
        using var installDir = new TempDir();
        var record = new RollbackRecord.RestoreEnv(
            "user", "Path", installDir.Path, PreviouslyAbsent: false);

        // Act
        var verdict = Anchor(installDir.Path).Check(record);

        // Assert
        verdict.RefusalReason.Should().BeNull();
    }

    // ---------------------------------------------------------------------------
    // Services.
    // ---------------------------------------------------------------------------

    [WindowsFact("Windows service registry")]
    public void Replay_refuses_a_service_the_app_never_installed()
    {
        // Arrange — sc stop + sc delete on a name of the attacker's choosing. The
        // service's own registered ImagePath is what ties it back to an install, and
        // this one runs out of System32. Predicate only: no sc.exe is spawned by this
        // test on any host, so a live service can never be stopped or deleted here.
        using var installDir = new TempDir();
        var (serviceName, imagePath) = FindStockService();

        // Act
        var verdict = Anchor(installDir.Path).Check(new RollbackRecord.RemoveService(serviceName));

        // Assert
        verdict.RefusalReason.Should().NotBeNull();
        verdict.RefusalReason!.Should().Contain(serviceName).And.Contain(imagePath,
            "the refusal must name the binary that proves the service is not ours");
    }

    [WindowsFact("Windows service registry")]
    public void Replay_allows_removing_a_service_that_does_not_exist()
    {
        // Arrange — the POSITIVE half. A rollback running after the service was already
        // removed (or before it was ever created) must not be reported as an attack.
        using var installDir = new TempDir();
        var record = new RollbackRecord.RemoveService(
            "SigilNoSuchService" + Guid.NewGuid().ToString("N"));

        // Act
        var verdict = Anchor(installDir.Path).Check(record);

        // Assert
        verdict.RefusalReason.Should().BeNull();
    }

    [WindowsTheory("Path parsing only")]
    // An ImagePath is a command line: quoted, unquoted, with arguments, NT-prefixed.
    // Getting this wrong refuses a real service teardown, which is the failure mode
    // that matters most.
    [InlineData(@"C:\Apps\Acme\svc.exe", true)]
    [InlineData(@"""C:\Apps\Acme\svc.exe""", true)]
    [InlineData(@"""C:\Apps\Acme\svc.exe"" -k net", true)]
    [InlineData(@"C:\Apps\Acme\sub dir\svc.exe -k net", true)]
    [InlineData(@"\??\C:\Apps\Acme\svc.exe", true)]
    [InlineData(@"C:\Apps\Acme\..\..\Windows\System32\svchost.exe", false)]
    [InlineData(@"C:\Windows\System32\svchost.exe -k netsvcs", false)]
    [InlineData(@"C:\Apps\AcmeEvil\svc.exe", false)]
    [InlineData("", false)]
    public void ImagePath_containment_handles_every_real_ImagePath_shape(string imagePath, bool inside)
    {
        // Arrange / Act — a pure string predicate over a literal install root; nothing
        // is read from disk or the registry.
        var result = ReplayAnchor.ImagePathIsInside(imagePath, @"C:\Apps\Acme");

        // Assert
        result.Should().Be(inside);
    }

    // ---------------------------------------------------------------------------
    // Replay mechanics.
    // ---------------------------------------------------------------------------

    [WindowsFact("Windows path semantics")]
    public async Task Replay_still_reverses_a_legitimate_record_inside_the_install_dir()
    {
        // Arrange — THE test that keeps anchoring honest, executed for real against
        // scratch coordinates: a file the install created inside install_dir, reversed
        // by exactly the record a file_copy step records.
        using var installDir = new TempDir();
        var installed = Path.Combine(installDir.Path, "app.exe");
        File.WriteAllText(installed, "payload");

        var journal = new RollbackJournal();
        journal.Append(new RollbackRecord.RestoreFile(installed, ExistedBefore: false, BackupPath: null));

        // Act
        var outcome = await journal.UndoAsync(
            ReplayAnchorage.ForInstallDir(installDir.Path), progress: null, ct: CancellationToken.None);

        // Assert
        outcome.RefusedRecords.Should().BeEmpty(
            "anchoring that breaks a real uninstall is worse than the bug it closes");
        File.Exists(installed).Should().BeFalse(
            "the legitimate in-install_dir record must actually have replayed");
    }

    [WindowsFact("Windows path semantics")]
    public async Task A_refused_record_is_skipped_and_the_rest_of_the_replay_continues()
    {
        // Arrange — one planted record and one legitimate one, both scratch. Aborting
        // would let a single planted record block a legitimate uninstall; silently
        // skipping would mask the attack. Neither.
        using var installDir = new TempDir();
        using var elsewhere = new TempDir();
        var installed = Path.Combine(installDir.Path, "app.exe");
        File.WriteAllText(installed, "payload");

        var journal = new RollbackJournal();
        journal.Append(new RollbackRecord.RestoreFile(installed, ExistedBefore: false, BackupPath: null));
        journal.Append(new RollbackRecord.UnregisterCom(Path.Combine(elsewhere.Path, "evil.dll")));

        var progress = new CapturingProgress();

        // Act
        var outcome = await journal.UndoAsync(
            ReplayAnchorage.ForInstallDir(installDir.Path), progress, CancellationToken.None);

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
        var outcome = await journal.UndoAsync(ReplayAnchorage.InProcess);

        // Assert
        outcome.RefusedRecords.Should().BeEmpty();
        File.Exists(outside).Should().BeFalse();
    }

    [Fact]
    public void ForInstallDir_rejects_an_empty_anchor_rather_than_silently_anchoring_to_nothing()
    {
        // "Anchored to nothing" would be indistinguishable from InProcess at the call
        // site, which is exactly the mistake the type exists to prevent.
        var act = () => ReplayAnchorage.ForInstallDir("   ");
        act.Should().Throw<ArgumentException>();
    }

    // ---------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------

    private static ReplayAnchor Anchor(string installDir) =>
        ReplayAnchor.For(ReplayAnchorage.ForInstallDir(installDir))!;

    private static RollbackRecord PathRecord(string type, string path) => type switch
    {
        "restore_file" => new RollbackRecord.RestoreFile(path, ExistedBefore: false, BackupPath: null),
        "remove_directory" => new RollbackRecord.RemoveDirectory(path),
        "delete_shortcut" => new RollbackRecord.DeleteShortcut(path),
        "remove_uninstaller" => new RollbackRecord.RemoveUninstaller(path),
        "restore_deleted_file" => new RollbackRecord.RestoreDeletedFile(path, path + ".stash"),
        "restore_deleted_directory" => new RollbackRecord.RestoreDeletedDirectory(path, path + ".stash"),
        "restore_config_file" => new RollbackRecord.RestoreConfigFile(path, null),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "unmapped record type"),
    };

    /// <summary>
    /// A stock Windows service and its <c>ImagePath</c>, for the negative service case.
    /// Probed rather than hardcoded so a box without the print spooler still exercises
    /// the rule instead of failing for the wrong reason. Read-only.
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

    /// <summary>Read-only: the literal (unexpanded) machine environment value.</summary>
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
