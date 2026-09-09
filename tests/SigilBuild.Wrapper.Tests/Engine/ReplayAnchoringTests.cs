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
using SigilBuild.Wrapper.Steps;
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
            ReplayAnchorage.ForInstallDir(installDir.Path, SignedDeclarations.None), progress: null, ct: CancellationToken.None);

        // Assert — the structured shape lane S5 consumes for R15, not just the prose.
        var refusal = outcome.RefusedRecords.Should().ContainSingle().Subject;
        refusal.RecordType.Should().Be("unregister_com");
        refusal.Target.Should().Be(evil);
        refusal.Code.Should().Be(ReplayRefusalCode.ComDllOutsideInstallDir);
        refusal.Message.Should().Contain("evil.dll",
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
        verdict.RefusalMessage.Should().NotBeNull();
        verdict.RefusalMessage!.Should().Contain("evil.dll");
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
        verdict.RefusalMessage.Should().BeNull();
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
        verdict.RefusalMessage.Should().NotBeNull();
        verdict.RefusalMessage!.Should().Contain("hosts",
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
        verdict.RefusalMessage.Should().NotBeNull();
        verdict.RefusalMessage!.Should().StartWith(type + " refused:").And.Contain("outside.dat");
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
        verdict.RefusalMessage.Should().BeNull(
            "a record targeting the install directory is exactly what a legitimate " +
            "uninstall replays");
    }

    // ---------------------------------------------------------------------------
    // The per-app state directory, and the content SOURCE of a record.
    // ---------------------------------------------------------------------------

    [WindowsFact("Windows-only state layout")]
    public void One_apps_journal_cannot_reach_another_apps_state_file()
    {
        // Arrange — the allowlist used to be the SHARED <StateRoot>\Sigil parent, so a
        // record in app A's journal could name app B's uninstall.json. Deleting it makes
        // B unremovable ("no uninstall state found"); overwriting it is worse, because
        // the elevated process writes inside the hardened directory and the result comes
        // out Administrators-owned — passing B's provenance gate on its next load, which
        // launders attacker content into trusted state. Predicate only; no file is
        // created, read or deleted.
        using var installDir = new TempDir();
        var appA = "sigil.appA." + Guid.NewGuid().ToString("N");
        var appB = "sigil.appB." + Guid.NewGuid().ToString("N");

        var anchor = AnchorFor(installDir.Path, appA, InstallScope.User);
        var victim = UninstallStateStore.PathFor(appB, InstallScope.User);

        // Act
        var deleted = anchor.Check(new RollbackRecord.RestoreFile(victim, false, null));
        var overwritten = anchor.Check(new RollbackRecord.RestoreDeletedFile(
            victim, Path.Combine(installDir.Path, "planted.json")));

        // Assert
        deleted.RefusalMessage.Should().NotBeNull();
        deleted.RefusalMessage!.Should().Contain(appB,
            "the state-directory allowance is this app's own directory, never the shared parent");
        overwritten.RefusalMessage.Should().NotBeNull();
        overwritten.RefusalMessage!.Should().Contain(appB);
    }

    [WindowsFact("Windows-only state layout")]
    public void An_app_can_still_reach_its_own_state_directory()
    {
        // Arrange — the paired positive. Narrowing to the per-app directory must not
        // narrow it to nothing.
        using var installDir = new TempDir();
        var appId = "sigil.own." + Guid.NewGuid().ToString("N");
        var own = Path.Combine(
            UninstallStateStore.DirectoryFor(appId, InstallScope.User), "scratch.dat");

        // Act
        var verdict = AnchorFor(installDir.Path, appId, InstallScope.User)
            .Check(new RollbackRecord.RestoreFile(own, false, null));

        // Assert
        verdict.RefusalMessage.Should().BeNull();
    }

    [WindowsFact("Windows-only state layout")]
    public void Without_an_app_id_no_state_directory_is_allowed_at_all()
    {
        // Arrange — ForInstallDir does not know whose state it is, so it allows none.
        // Omission fails CLOSED, which is why the narrower overload can be optional.
        using var installDir = new TempDir();
        var appId = "sigil.own." + Guid.NewGuid().ToString("N");
        var own = Path.Combine(
            UninstallStateStore.DirectoryFor(appId, InstallScope.User), "scratch.dat");

        // Act
        var verdict = Anchor(installDir.Path)
            .Check(new RollbackRecord.RestoreFile(own, false, null));

        // Assert
        verdict.RefusalMessage.Should().NotBeNull();
    }

    [WindowsTheory("Windows path semantics")]
    [InlineData("restore_file")]
    [InlineData("restore_deleted_file")]
    [InlineData("restore_deleted_directory")]
    [InlineData("restore_config_file")]
    public void A_contained_destination_fed_from_an_uncontained_source_is_refused(string type)
    {
        // Arrange — the register's wording for these rows is "arbitrary file / tree write
        // FROM AN ATTACKER-CHOSEN STASH". Checking only the destination is a narrower
        // guarantee than that: the bytes still come from wherever the record says.
        //
        // The source is CREATED here, which is what makes this the planted case rather
        // than the ordinary one: a persisted record whose stash was already reclaimed
        // names a path that no longer exists, and that must stay quiet (see
        // A_persisted_record_whose_stash_was_reclaimed_replays_silently).
        using var installDir = new TempDir();
        using var elsewhere = new TempDir();
        var destination = Path.Combine(installDir.Path, "app.exe");
        var source = Path.Combine(elsewhere.Path, "evil.bin");
        if (type == "restore_deleted_directory")
        {
            Directory.CreateDirectory(source);
        }
        else
        {
            File.WriteAllText(source, "attacker bytes");
        }

        // Act
        var verdict = Anchor(installDir.Path).Check(SourcedRecord(type, destination, source));

        // Assert
        verdict.Refusal.Should().NotBeNull();
        verdict.Refusal!.Code.Should().Be(ReplayRefusalCode.ContentSourceOutsideInstallRoots);
        verdict.Refusal.Message.Should().Contain("evil.bin");
    }

    [WindowsTheory("Windows path semantics")]
    [InlineData("restore_file")]
    [InlineData("restore_deleted_file")]
    [InlineData("restore_deleted_directory")]
    [InlineData("restore_config_file")]
    public void A_persisted_record_whose_stash_was_reclaimed_replays_silently(string type)
    {
        // Arrange — the shape EVERY persisted file_delete / directory_delete / ini_write /
        // json_edit / xml_edit record has after a successful install: the stash under
        // %TEMP% was reclaimed at commit, but the record still names it. Refusing on the
        // path alone made a healthy uninstall emit the very log line the documentation
        // tells publishers to investigate.
        using var installDir = new TempDir();
        var destination = Path.Combine(installDir.Path, "app.exe");
        var reclaimed = Path.Combine(
            Path.GetTempPath(), "sigil-fd-" + Guid.NewGuid().ToString("N"));
        File.Exists(reclaimed).Should().BeFalse("the stash is gone, which is the whole point");

        // Act
        var verdict = Anchor(installDir.Path).Check(SourcedRecord(type, destination, reclaimed));

        // Assert
        verdict.Refusal.Should().BeNull(
            "the undo is already a no-op, so a healthy uninstall must log nothing");

        // …and the source it would read has been rewritten to something that cannot appear
        // later, so a file materialising between this check and the copy is not read.
        SourceOf(verdict.Record).Should().NotBe(reclaimed);
    }

    [WindowsTheory("Windows path semantics")]
    [InlineData("restore_file")]
    [InlineData("restore_deleted_file")]
    [InlineData("restore_deleted_directory")]
    [InlineData("restore_config_file")]
    public void A_source_inside_the_anchor_still_replays(string type)
    {
        // Arrange — the paired positive, and the reason checking the source costs nothing:
        // FileCopyStep writes its backup as "<destination>.sigil-bak", i.e. beside the
        // file it is backing up, so it is anchored exactly when the destination is.
        using var installDir = new TempDir();
        var destination = Path.Combine(installDir.Path, "app.exe");

        // Act
        var verdict = Anchor(installDir.Path)
            .Check(SourcedRecord(type, destination, destination + ".sigil-bak"));

        // Assert
        verdict.RefusalMessage.Should().BeNull(
            "a backup written beside its own destination is the shape every real " +
            "file_copy rollback has");
    }

    [WindowsTheory("Windows path semantics")]
    [InlineData("file_delete")]
    [InlineData("directory_delete")]
    [InlineData("ini_write")]
    [InlineData("json_edit")]
    [InlineData("xml_edit")]
    public async Task A_legitimate_uninstall_of_a_stash_backed_step_replays_without_a_single_refusal(
        string stepType)
    {
        // Arrange — the end-to-end shape of Important 1, driven through the REAL steps
        // rather than hand-built records: run the step, then reclaim the transient stash
        // exactly as the engine does when the install commits, then replay the persisted
        // journal ANCHORED. Every one of these five step types stashes under %TEMP%, so a
        // source check that looked only at the path refused all of them on a healthy
        // uninstall.
        using var installDir = new TempDir();
        var journal = new RollbackJournal();
        var step = StepFactory.Create(StashBackedSpec(stepType, installDir.Path));

        var ran = await step.RunAsync(StepContext.Empty, journal, CancellationToken.None);
        ran.Success.Should().BeTrue("the fixture needs the step to have actually run");
        journal.Records.Should().NotBeEmpty();

        // What InstallSession does on commit, before the journal is persisted.
        journal.DiscardTransientStashes();

        var progress = new CapturingProgress();

        // Act
        var outcome = await journal.UndoAsync(
            ReplayAnchorage.ForInstall(installDir.Path, "sigil.stash.app", InstallScope.User, SignedDeclarations.None),
            progress,
            CancellationToken.None);

        // Assert
        outcome.RefusedRecords.Should().BeEmpty(
            "a healthy uninstall of a stash-backed step must not report a security refusal " +
            "— it is the first thing the documentation tells a publisher to investigate");
        progress.Messages.Should().NotContain(
            m => m.StartsWith("refused:", StringComparison.Ordinal));
    }

    [WindowsFact("Windows shell folders")]
    public void The_all_users_startup_folder_is_not_reachable_even_though_the_start_menu_is()
    {
        // Arrange — Startup is INSIDE the Start Menu subtree and is a per-logon execution
        // surface, so nothing may WRITE there. The treatment is deliberately asymmetric:
        // shortcut_create accepts an explicit location, a publisher may legitimately place
        // a startup shortcut, and refusing its removal would leave that shortcut
        // auto-starting after uninstall — the anchor creating the persistence it exists to
        // prevent.
        using var installDir = new TempDir();
        var startMenu = ScopeLayout.For(InstallScope.Machine).StartMenuFolder;
        var anchor = Anchor(installDir.Path);
        var startupLnk = Path.Combine(startMenu, "Programs", "Startup", "acme.lnk");

        // Act
        var ordinary = anchor.Check(new RollbackRecord.DeleteShortcut(
            Path.Combine(startMenu, "Programs", "Acme", "Acme.lnk")));
        var removingAStartupShortcut = anchor.Check(new RollbackRecord.DeleteShortcut(startupLnk));
        var writingIntoStartup = anchor.Check(new RollbackRecord.RestoreDeletedFile(
            startupLnk, Path.Combine(installDir.Path, "planted.lnk")));

        // Assert
        removingAStartupShortcut.RefusalMessage.Should().BeNull(
            "an installer may place a startup shortcut, and its removal at uninstall must " +
            "replay or the app keeps auto-starting after it has been removed");
        writingIntoStartup.RefusalMessage.Should().NotBeNull();
        writingIntoStartup.RefusalMessage!.Should().Contain("never write to it",
            "a planted record must not be able to populate a per-logon execution surface");
        ordinary.RefusalMessage.Should().BeNull(
            "an ordinary Start Menu shortcut must still be removable");
    }

    [WindowsFact("Windows shell folders")]
    public void A_machine_scope_replay_cannot_touch_the_per_user_shortcut_folders()
    {
        // Arrange — when the scope is known, only that scope's shortcut folders are in
        // range. A machine uninstall has no business deleting a per-user shortcut.
        using var installDir = new TempDir();
        var userDesktop = Path.Combine(
            ScopeLayout.For(InstallScope.User).DesktopFolder, "Acme.lnk");

        // Act
        var machineAnchor = AnchorFor(installDir.Path, "sigil.x", InstallScope.Machine);
        var userAnchor = AnchorFor(installDir.Path, "sigil.x", InstallScope.User);

        // Assert
        machineAnchor.Check(new RollbackRecord.DeleteShortcut(userDesktop))
            .RefusalMessage.Should().NotBeNull();
        userAnchor.Check(new RollbackRecord.DeleteShortcut(userDesktop))
            .RefusalMessage.Should().BeNull("the same record in its own scope must replay");
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
                ReplayAnchorage.ForInstallDir(installDir.Path, SignedDeclarations.None), progress: null, ct: CancellationToken.None);

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

        // Act — the manifest DECLARES the key under test (R51), so the refusal can only
        // come from the space and shape rules this test exists for. Declaring a
        // dangerous coordinate must not be a way to have it replayed.
        var verdict = AnchorDeclaring(installDir.Path, hive, key).Check(record);

        // Assert
        verdict.RefusalMessage.Should().NotBeNull();
        verdict.RefusalMessage!.Should().Contain(key);
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
        // progid, its own file extension, and its own ARP row. Since R51 those keys must
        // be DECLARED to replay — which is what the step that wrote them does.
        using var installDir = new TempDir();
        var record = new RollbackRecord.RestoreRegistryValue(
            hive, key, "Installed", "default", "REG_SZ", null, PreviouslyAbsent: true);

        // Act
        var verdict = AnchorDeclaring(installDir.Path, hive, key).Check(record);

        // Assert
        verdict.RefusalMessage.Should().BeNull();
    }

    [WindowsFact("Windows registry semantics")]
    public void A_per_user_app_restoring_its_own_file_association_is_allowed()
    {
        // Arrange — THE positive for the shape rule, and the reason it is not simply
        // "deny every shell\...\command". Registering a file type is what installers do;
        // the command points at the app's own exe inside install_dir, quoted with the
        // usual "%1" argument. HKCU, because a per-user install legitimately lands in a
        // user-writable directory (%LocalAppData%\Programs) — requiring admin-only there
        // would refuse every per-user association on uninstall.
        using var installDir = new TempDir();
        var record = new RollbackRecord.RestoreRegistryValue(
            "HKCU",
            @"Software\Classes\Acme.Document\shell\open\command",
            Name: "",
            View: "default",
            PriorTypeStr: "REG_SZ",
            PriorValue: $"\"{Path.Combine(installDir.Path, "acme.exe")}\" \"%1\"",
            PreviouslyAbsent: false);

        // Act — declared, because the manifest's file-association step names this key.
        var verdict = AnchorDeclaring(
            installDir.Path, "HKCU", @"Software\Classes\Acme.Document").Check(record);

        // Assert
        verdict.RefusalMessage.Should().BeNull(
            "denying the shape outright would break every installer that registers a " +
            "file type — the value must be checked, not the key alone");
    }

    [WindowsFact("Windows registry semantics")]
    public void A_machine_app_restoring_its_own_file_association_from_an_admin_only_dir_is_allowed()
    {
        // Arrange — the machine-hive positive. %ProgramFiles% stands in for a real
        // machine install directory: admin-only writable, which is what a machine-wide
        // execution mapping is required to point into. Read-only; no file is created.
        var installDir = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        StateDirectorySecurity.IsAdminOnlyWritable(installDir).Should().BeTrue(
            "the fixture needs a genuinely admin-only install directory");

        var record = new RollbackRecord.RestoreRegistryValue(
            "HKLM",
            @"Software\Classes\Acme.Document\shell\open\command",
            Name: "",
            View: "default",
            PriorTypeStr: "REG_SZ",
            PriorValue: $"\"{Path.Combine(installDir, "acme.exe")}\" \"%1\"",
            PreviouslyAbsent: false);

        // Act — declared, as the manifest's file-association step declares it.
        var verdict = AnchorDeclaring(
            installDir, "HKLM", @"Software\Classes\Acme.Document").Check(record);

        // Assert
        verdict.RefusalMessage.Should().BeNull(
            "a machine install reversing its own association to its own binary in an " +
            "admin-only directory is the ordinary case and must replay");
    }

    [WindowsFact("Windows registry semantics")]
    public void A_machine_execution_mapping_into_a_user_writable_install_dir_is_refused()
    {
        // Arrange — the hole the categorical rewrite opened. Once the progid deny-list
        // went away, an execution mapping was accepted on containment alone, so a value
        // INSIDE the anchor passed. If the anchor is a directory an unprivileged user can
        // write — a /D= into %TEMP%, or a recorded install dir that squeaks past the
        // anchor floor — that hands exefile\shell\open\command to an exe that user
        // controls, machine-wide. An execution mapping names a binary the system will
        // run, exactly like a machine PATH entry, so it must meet the same standard.
        // Predicate only; HKLM is never opened.
        using var installDir = new TempDir();
        StateDirectorySecurity.IsAdminOnlyWritable(installDir.Path).Should().BeFalse(
            "the fixture must really be a user-writable directory, or this proves nothing");

        var evil = Path.Combine(installDir.Path, "evil.exe");
        var record = new RollbackRecord.RestoreRegistryValue(
            "HKLM",
            @"Software\Classes\exefile\shell\open\command",
            Name: "",
            View: "default",
            PriorTypeStr: "REG_SZ",
            PriorValue: $"\"{evil}\" \"%1\"",
            PreviouslyAbsent: false);

        // Act — the key is DECLARED, so R51's allowlist passes it through and the
        // execution-mapping rule is what refuses it. This is the layering stated as a
        // test: a manifest declaration does not buy an unowned machine-wide mapping.
        var verdict = AnchorDeclaring(
            installDir.Path, "HKLM", @"Software\Classes\exefile\shell\open\command").Check(record);

        // Assert
        verdict.RefusalMessage.Should().NotBeNull();
        verdict.RefusalMessage!.Should().Contain("evil.exe").And.Contain("administrators",
            "being inside the anchor is not enough for a machine-wide execution mapping; " +
            "the target must also be one no non-administrator can rewrite");
    }

    [WindowsTheory("Windows registry semantics")]
    // A subkey or value name that happens to be spelled like an execution-mapping
    // segment, in ordinary application space. None of these has execution semantics, and
    // refusing them strands a value on every legitimate uninstall AND pollutes the
    // RefusedRecords list lane S5 reads for R15.
    [InlineData(@"Software\Acme\App\command")]
    [InlineData(@"Software\Acme\App\TreatAs")]
    [InlineData(@"Software\Acme\App\LocalServer")]
    [InlineData(@"Software\Acme\App\MCI32")]
    [InlineData(@"Software\Acme\App\Drivers32")]
    [InlineData(@"Software\Acme\App\shellex")]
    [InlineData(@"Software\Acme\shell\App")]
    public void An_ordinary_key_that_merely_spells_like_an_execution_mapping_is_not_one(string key)
    {
        // Arrange — the value points OUTSIDE the install dir on purpose: if the key were
        // wrongly classified as an execution mapping, the value check would refuse it,
        // so this fails loudly rather than passing for the wrong reason.
        using var installDir = new TempDir();
        using var elsewhere = new TempDir();
        var record = new RollbackRecord.RestoreRegistryValue(
            "HKLM",
            key,
            Name: "",
            View: "default",
            PriorTypeStr: "REG_SZ",
            PriorValue: Path.Combine(elsewhere.Path, "data.txt"),
            PreviouslyAbsent: false);

        // Act
        var verdict = AnchorDeclaring(installDir.Path, "HKLM", key).Check(record);

        // Assert
        verdict.RefusalMessage.Should().BeNull(
            "the shape must be matched by structural position, not by the word: a plain " +
            "application key carries no execution semantics and must replay");
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

        // Act — declared, so the refusal comes from the value, not from the allowlist.
        var verdict = AnchorDeclaring(
            installDir.Path, "HKLM", @"Software\Classes\Acme.Document").Check(record);

        // Assert
        verdict.RefusalMessage.Should().NotBeNull();
        verdict.RefusalMessage!.Should().Contain("evil.exe").And.Contain("execution mapping");
    }

    [WindowsFact("Windows registry semantics")]
    public void RestoreRegistryKey_obeys_the_same_rule_as_RestoreRegistryValue()
    {
        // Arrange — the key-level record was never covered; it writes just as much. Both
        // coordinates are declared, so the refusal below is the space rule's doing.
        using var installDir = new TempDir();
        var anchor = AnchorDeclaring(
            installDir.Path,
            "HKLM",
            @"SYSTEM\CurrentControlSet\Services\Spooler",
            @"Software\Acme\App");
        var snapshots = Array.Empty<RegistryValueSnapshot>();

        // Act
        var refused = anchor.Check(new RollbackRecord.RestoreRegistryKey(
            "HKLM", @"SYSTEM\CurrentControlSet\Services\Spooler", "default", snapshots, false));
        var allowed = anchor.Check(new RollbackRecord.RestoreRegistryKey(
            "HKLM", @"Software\Acme\App", "default", snapshots, false));

        // Assert
        refused.RefusalMessage.Should().NotBeNull();
        refused.RefusalMessage!.Should().Contain("Services");
        allowed.RefusalMessage.Should().BeNull();
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
        verdict.RefusalMessage.Should().NotBeNull();
        verdict.RefusalMessage!.Should().Contain(@"C:\Users\Public\evil",
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
        verdict.RefusalMessage.Should().NotBeNull();
        verdict.RefusalMessage!.Should().Contain(installDir.Path,
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
        verdict.RefusalMessage.Should().BeNull(
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
        verdict.RefusalMessage.Should().NotBeNull();
        verdict.RefusalMessage!.Should().Contain($"deleting {scope}-scope 'Path'",
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
        verdict.RefusalMessage.Should().NotBeNull();
        verdict.RefusalMessage!.Should().Contain("could not be read",
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
        verdict.RefusalMessage.Should().BeNull();
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
        verdict.RefusalMessage.Should().NotBeNull();
        verdict.RefusalMessage!.Should().Contain(@"C:\Users\Public\evil");
    }

    [WindowsFact("Windows registry semantics")]
    public void A_set_style_env_restore_replays_when_the_install_owned_the_whole_variable()
    {
        // Arrange — `action: set` is env_set's DOCUMENTED DEFAULT and replaces the value,
        // so the prior value is by construction absent from the current one. The
        // append/prepend model alone can therefore never accept it: a manifest that
        // repoints JAVA_HOME at its own JRE would have its restore refused, leaving
        // JAVA_HOME dangling at the just-deleted install directory and reporting a REFUSED
        // record on a completely legitimate uninstall.
        //
        // The fixture is a GUID-named scratch variable in the CURRENT USER's Environment
        // key — never a system variable, never machine scope — created here and removed in
        // the finally, following the pattern ReinstallIdempotencyTests already uses.
        using var installDir = new TempDir();
        var name = "SIGIL_ANCHOR_SET_" + Guid.NewGuid().ToString("N");
        var ownedByInstall = Path.Combine(installDir.Path, "jre");

        try
        {
            WriteUserEnv(name, ownedByInstall);

            var record = new RollbackRecord.RestoreEnv(
                "user", name, PriorValue: @"C:\Program Files\Java\jdk-21", PreviouslyAbsent: false);

            // Act
            var verdict = Anchor(installDir.Path).Check(record);

            // Assert
            verdict.RefusalMessage.Should().BeNull(
                "the variable's current value is wholly a directory this install owns, so " +
                "this install had taken it over and putting back what preceded it is the undo");
        }
        finally
        {
            DeleteUserEnv(name);
        }
    }

    [WindowsFact("Windows registry semantics")]
    public void A_set_style_env_restore_is_refused_when_the_install_did_not_own_the_variable()
    {
        // Arrange — the paired negative, and what stops the `set` model from becoming a
        // way to write anything anywhere: the current value must be one this install
        // wholly owns. Here it points somewhere else entirely, so neither model applies.
        using var installDir = new TempDir();
        using var elsewhere = new TempDir();
        var name = "SIGIL_ANCHOR_SET_" + Guid.NewGuid().ToString("N");

        try
        {
            WriteUserEnv(name, Path.Combine(elsewhere.Path, "jre"));

            var record = new RollbackRecord.RestoreEnv(
                "user", name, PriorValue: @"C:\Users\Public\evil", PreviouslyAbsent: false);

            // Act
            var verdict = Anchor(installDir.Path).Check(record);

            // Assert
            verdict.Refusal.Should().NotBeNull();
            verdict.Refusal!.Code.Should().Be(ReplayRefusalCode.EnvironmentIntroducesForeignEntry);
            verdict.Refusal.Message.Should().Contain(@"C:\Users\Public\evil");
        }
        finally
        {
            DeleteUserEnv(name);
        }
    }

    [WindowsTheory("Windows registry semantics")]
    [InlineData("machine", "Path")]
    [InlineData("user", "Path")]
    [InlineData("machine", "ComSpec")]
    public void The_set_model_never_applies_to_a_system_critical_variable(string scope, string name)
    {
        // Arrange — no installer legitimately `set`s PATH, so the subset rule must keep
        // governing it however install-owned the current value happens to look.
        //
        // The assertion is on the CODE, not merely on "a refusal happened", and that is
        // what makes the guard falsifiable. Asserting a refusal alone could not fail:
        // a system-critical variable's value is never wholly install-owned anyway, so with
        // the guard deleted the set model would refuse it on that instead and the test
        // would stay green. The guard now runs first and carries its own code, so deleting
        // it changes EnvironmentSystemVariableNotReplaceable into
        // EnvironmentIntroducesForeignEntry and this fails. Read-only throughout.
        using var installDir = new TempDir();
        var record = new RollbackRecord.RestoreEnv(
            scope, name, PriorValue: @"C:\Users\Public\evil", PreviouslyAbsent: false);

        // Act
        var verdict = Anchor(installDir.Path).Check(record);

        // Assert
        verdict.Refusal.Should().NotBeNull();
        verdict.Refusal!.Code.Should().Be(
            ReplayRefusalCode.EnvironmentSystemVariableNotReplaceable,
            "the exclusion must be observable, or it is untested");
    }

    [WindowsFact("Windows registry semantics")]
    public void Two_records_cannot_compose_into_a_whole_value_env_write()
    {
        // Arrange — the composition Important 2 reported. Each record is individually
        // plausible; the pair is not. Record A restores an install-owned value (allowed by
        // the subset model, because the target is inside install_dir), which — if the set
        // model consulted the LIVE value — would manufacture exactly the "this install owns
        // the whole variable" precondition record B needs to write anything at all. For a
        // machine variable such as COR_PROFILER_PATH that is code injection into every
        // .NET process on the box.
        //
        // User scope and a GUID-named scratch variable, because the point is the ordering
        // rule rather than the hive, and this must never touch a real machine variable.
        // The mid-sequence write is done by the test rather than by replaying record A, so
        // nothing is broadcast and no real undo runs.
        using var installDir = new TempDir();
        var name = "SIGIL_ANCHOR_COMP_" + Guid.NewGuid().ToString("N");
        var installOwned = Path.Combine(installDir.Path, "profiler.dll");

        try
        {
            WriteUserEnv(name, @"C:\Users\Public\seed");   // pre-replay: NOT install-owned
            var anchor = Anchor(installDir.Path);

            // Act — record A is legitimate on its own.
            var first = anchor.Check(new RollbackRecord.RestoreEnv(
                "user", name, PriorValue: installOwned, PreviouslyAbsent: false));

            // …and now the variable holds what record A would have written.
            WriteUserEnv(name, installOwned);

            var second = anchor.Check(new RollbackRecord.RestoreEnv(
                "user", name, PriorValue: @"C:\Users\Public\evil.dll", PreviouslyAbsent: false));

            // Assert
            first.RefusalMessage.Should().BeNull("record A is individually plausible");
            second.Refusal.Should().NotBeNull(
                "the set model's premise must be the value the variable held BEFORE the " +
                "replay began; read live, a record can manufacture the precondition for " +
                "the next one and the pair composes into a primitive neither record is");
            second.Refusal!.Code.Should().Be(ReplayRefusalCode.EnvironmentIntroducesForeignEntry);
        }
        finally
        {
            DeleteUserEnv(name);
        }
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
        verdict.RefusalMessage.Should().BeNull();
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
        verdict.RefusalMessage.Should().NotBeNull();
        verdict.RefusalMessage!.Should().Contain(serviceName).And.Contain(imagePath,
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
        verdict.RefusalMessage.Should().BeNull();
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
            ReplayAnchorage.ForInstallDir(installDir.Path, SignedDeclarations.None), progress: null, ct: CancellationToken.None);

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
            ReplayAnchorage.ForInstallDir(installDir.Path, SignedDeclarations.None), progress, CancellationToken.None);

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

    // ---------------------------------------------------------------------------
    // The RefusedRecords contract (lane S5 consumes this in Stage 2 for R15).
    // ---------------------------------------------------------------------------

    [WindowsFact("Windows path + registry semantics")]
    public void Every_refusal_carries_a_record_type_a_target_and_a_code_not_just_prose()
    {
        // Arrange — one refusal of each kind, so a consumer never has to parse Message
        // to learn what happened. Predicate only; nothing is replayed.
        using var installDir = new TempDir();
        using var elsewhere = new TempDir();
        var anchor = Anchor(installDir.Path);
        var outsidePath = Path.Combine(elsewhere.Path, "outside.dat");
        var (serviceName, _) = FindStockService();

        // Act
        var path = anchor.Check(new RollbackRecord.RestoreFile(outsidePath, false, null));
        var registry = anchor.Check(new RollbackRecord.RestoreRegistryValue(
            "HKLM", @"SYSTEM\CurrentControlSet\Services\Spooler", "ImagePath", "default",
            "REG_SZ", @"C:\Users\Public\evil.exe", false));
        // Declared (R51), so this record reaches the execution-mapping rule and carries
        // that rule's code rather than the allowlist's.
        var mapping = AnchorDeclaring(
                installDir.Path, "HKLM", @"Software\Classes\exefile\shell\open\command")
            .Check(new RollbackRecord.RestoreRegistryValue(
                "HKLM", @"Software\Classes\exefile\shell\open\command", "", "default",
                "REG_SZ", Path.Combine(elsewhere.Path, "evil.exe"), false));
        var undeclared = anchor.Check(new RollbackRecord.RestoreRegistryValue(
            "HKCU", @"Software\Acme\App", "Installed", "default", "REG_SZ", null, true));
        var env = anchor.Check(new RollbackRecord.RestoreEnv(
            "machine", "Path", @"C:\Users\Public\evil;" + ReadMachineEnv("Path"), false));
        var service = anchor.Check(new RollbackRecord.RemoveService(serviceName));
        var com = anchor.Check(new RollbackRecord.UnregisterCom(
            Path.Combine(elsewhere.Path, "evil.dll")));

        // Assert
        path.Refusal.Should().NotBeNull();
        path.Refusal!.RecordType.Should().Be("restore_file");
        path.Refusal.Target.Should().Be(outsidePath);
        path.Refusal.Code.Should().Be(ReplayRefusalCode.PathOutsideInstallRoots);

        registry.Refusal.Should().NotBeNull();
        registry.Refusal!.RecordType.Should().Be("restore_registry_value");
        registry.Refusal.Target.Should().Be(@"HKLM\SYSTEM\CurrentControlSet\Services\Spooler");
        registry.Refusal.Code.Should().Be(ReplayRefusalCode.RegistryOutsideApplicationSpace);

        mapping.Refusal.Should().NotBeNull();
        mapping.Refusal!.Target.Should().Be(@"HKLM\Software\Classes\exefile\shell\open\command");
        mapping.Refusal.Code.Should().Be(ReplayRefusalCode.ExecutionMappingNotOwned);

        undeclared.Refusal.Should().NotBeNull();
        undeclared.Refusal!.RecordType.Should().Be("restore_registry_value");
        undeclared.Refusal.Target.Should().Be(@"HKCU\Software\Acme\App");
        undeclared.Refusal.Code.Should().Be(ReplayRefusalCode.RegistryKeyNotDeclared);

        env.Refusal.Should().NotBeNull();
        env.Refusal!.RecordType.Should().Be("restore_env");
        env.Refusal.Target.Should().Be("env:machine:Path");
        env.Refusal.Code.Should().Be(ReplayRefusalCode.EnvironmentSystemVariableNotReplaceable);

        service.Refusal.Should().NotBeNull();
        service.Refusal!.RecordType.Should().Be("remove_service");
        service.Refusal.Target.Should().Be(serviceName);
        service.Refusal.Code.Should().Be(ReplayRefusalCode.ServiceNotOwned);

        com.Refusal.Should().NotBeNull();
        com.Refusal!.RecordType.Should().Be("unregister_com");
        com.Refusal.Code.Should().Be(ReplayRefusalCode.ComDllOutsideInstallDir);

        // …and every one still carries the operator-facing line the /LOG file needs.
        foreach (var refusal in new[]
                 {
                     path.Refusal, registry.Refusal, mapping.Refusal,
                     env.Refusal, service.Refusal, com.Refusal,
                 })
        {
            refusal.Message.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void ForInstallDir_rejects_an_empty_anchor_rather_than_silently_anchoring_to_nothing()
    {
        // "Anchored to nothing" would be indistinguishable from InProcess at the call
        // site, which is exactly the mistake the type exists to prevent.
        var act = () => ReplayAnchorage.ForInstallDir("   ", SignedDeclarations.None);
        act.Should().Throw<ArgumentException>();
    }

    // ---------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------

    private static ReplayAnchor Anchor(string installDir) =>
        ReplayAnchor.For(ReplayAnchorage.ForInstallDir(installDir, SignedDeclarations.None))!;

    /// <summary>
    /// An anchor whose signed manifest declares <paramref name="keys"/> in
    /// <paramref name="hive"/> (R51). Every registry test that expects a record to be
    /// judged on its SPACE or its SHAPE now has to declare the key first — otherwise the
    /// allowlist refuses it earlier and the test would pass without exercising the rule
    /// it was written for. Declaring a key is exactly what a manifest carrying a
    /// <c>registry_write</c> step for it does; it is not an escape hatch, which is what
    /// the deny-list and execution-mapping negatives below now also prove.
    /// </summary>
    private static ReplayAnchor AnchorDeclaring(string installDir, string hive, params string[] keys) =>
        ReplayAnchor.For(ReplayAnchorage.ForInstallDir(
            installDir,
            SignedDeclarations.ForLiterals(
                null,
                Array.ConvertAll(keys, k => new DeclaredRegistryKey(hive, k)))))!;

    private static ReplayAnchor AnchorFor(string installDir, string appId, InstallScope scope) =>
        ReplayAnchor.For(ReplayAnchorage.ForInstall(
            installDir, appId, scope, SignedDeclarations.None))!;

    /// <summary>
    /// A real step spec of <paramref name="stepType"/> whose rollback record stashes the
    /// prior content under <c>%TEMP%</c>, with its target materialised inside
    /// <paramref name="installDir"/>.
    /// </summary>
    private static InstallStep StashBackedSpec(string stepType, string installDir)
    {
        var target = Path.Combine(installDir, "config.dat");
        switch (stepType)
        {
            case "file_delete":
                File.WriteAllText(target, "prior");
                return new InstallStep.FileDelete(
                    "s", target, IfMissing: "fail", When: null, OnFailure: OnFailure.Fail);

            case "directory_delete":
                var dir = Path.Combine(installDir, "subtree");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "f.txt"), "prior");
                return new InstallStep.DirectoryDelete(
                    "s", dir, Recursive: true, When: null, OnFailure: OnFailure.Fail);

            case "ini_write":
                File.WriteAllText(target, "[s]\nk=old\n");
                return new InstallStep.IniWrite(
                    "s", target, "s", "k", "new", CreateIfMissing: false,
                    When: null, OnFailure: OnFailure.Fail);

            case "json_edit":
                File.WriteAllText(target, "{\"k\":\"old\"}");
                return new InstallStep.JsonEdit(
                    "s", target, "/k", "new", CreateIfMissing: false,
                    When: null, OnFailure: OnFailure.Fail);

            case "xml_edit":
                File.WriteAllText(target, "<root><k>old</k></root>");
                return new InstallStep.XmlEdit(
                    "s", target, "/root/k", Attribute: null, Value: "new",
                    CreateIfMissing: false, When: null, OnFailure: OnFailure.Fail);

            default:
                throw new ArgumentOutOfRangeException(nameof(stepType), stepType, "unmapped step type");
        }
    }

    /// <summary>The content source a verdict's record would actually read.</summary>
    private static string? SourceOf(RollbackRecord record) => record switch
    {
        RollbackRecord.RestoreFile r => r.BackupPath,
        RollbackRecord.RestoreDeletedFile r => r.StashPath,
        RollbackRecord.RestoreDeletedDirectory r => r.StashPath,
        RollbackRecord.RestoreConfigFile r => r.StashPath,
        _ => null,
    };

    /// <summary>
    /// A record of <paramref name="type"/> writing to <paramref name="destination"/> with
    /// its bytes coming from <paramref name="source"/>.
    /// </summary>
    private static RollbackRecord SourcedRecord(string type, string destination, string source) =>
        type switch
        {
            "restore_file" => new RollbackRecord.RestoreFile(
                destination, ExistedBefore: true, BackupPath: source),
            "restore_deleted_file" => new RollbackRecord.RestoreDeletedFile(destination, source),
            "restore_deleted_directory" =>
                new RollbackRecord.RestoreDeletedDirectory(destination, source),
            "restore_config_file" => new RollbackRecord.RestoreConfigFile(destination, source),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "unmapped record type"),
        };

    /// <summary>
    /// Create a GUID-named scratch variable in the CURRENT USER's <c>Environment</c> key.
    /// Never a system variable and never machine scope; removed by
    /// <see cref="DeleteUserEnv"/> in the caller's <c>finally</c>. Same pattern as
    /// <c>ReinstallIdempotencyTests</c>, which predates this lane.
    /// </summary>
    private static void WriteUserEnv(string name, string value)
    {
        using var key = Registry.CurrentUser.OpenSubKey(UserEnvironmentKey, writable: true);
        key.Should().NotBeNull("the fixture needs the per-user Environment key");
        key!.SetValue(name, value, RegistryValueKind.String);
    }

    private static void DeleteUserEnv(string name)
    {
#pragma warning disable CA1031 // Best-effort removal of the test's own scratch variable.
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UserEnvironmentKey, writable: true);
            key?.DeleteValue(name, throwOnMissingValue: false);
        }
        catch
        {
            // Best-effort.
        }
#pragma warning restore CA1031
    }

    private const string UserEnvironmentKey = "Environment";

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
