namespace SigilBuild.Wrapper.Tests.Engine;

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Tests.Helpers;
using Xunit;

/// <summary>
/// R1: the machine-scope state directory is the trust boundary for elevated
/// replay. These tests assert it is refused when an unprivileged user could
/// have authored it, and — just as importantly — that the predicate still says
/// <c>true</c> for the real directories a legitimate machine install uses, so a
/// regression to a constant-false predicate cannot pass CI.
/// </summary>
/// <remarks>
/// <para>
/// <c>[SupportedOSPlatform("windows")]</c> satisfies CA1416 for the
/// <c>StateDirectorySecurity</c> call sites (the same pattern
/// <c>Helpers/TestRegistry.cs</c> uses); <c>[WindowsFact]</c> is what makes the
/// tests report Skipped — rather than pass vacuously — on a non-Windows host.
/// </para>
/// <para>
/// The whole file runs UNELEVATED. That constrains what can be asserted about
/// <c>CreateHardened</c>: a directory an unelevated process creates is owned by
/// that user, so it can never satisfy the strengthened <c>IsTrusted</c> however
/// correct <c>CreateHardened</c> is. The tests therefore assert the DACL it
/// produces — protected, admin-only — not a round trip through <c>IsTrusted</c>.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public class StateProvenanceTests
{
    private static readonly SecurityIdentifier LocalSystem =
        new(WellKnownSidType.LocalSystemSid, null);

    private static readonly SecurityIdentifier Administrators =
        new(WellKnownSidType.BuiltinAdministratorsSid, null);

    private static readonly SecurityIdentifier Users =
        new(WellKnownSidType.BuiltinUsersSid, null);

    [WindowsFact("Windows ACL APIs")]
    public void Untrusted_state_directory_is_not_trusted()
    {
        // Arrange — a directory created with default (inherited, user-owned)
        // ACLs under the test temp dir, exactly like the current bare
        // Directory.CreateDirectory does under %ProgramData%.
        using var temp = new TempDir();
        var dir = Path.Combine(temp.Path, "sigil-state");
        Directory.CreateDirectory(dir);

        // Act
        var trusted = StateDirectorySecurity.IsTrusted(dir);

        // Assert
        trusted.Should().BeFalse(
            "a directory owned by the current (non-SYSTEM) user must never be " +
            "trusted to supply records for elevated replay");
    }

    [WindowsFact("Windows ACL APIs")]
    public void IsTrusted_fails_closed_on_a_missing_directory()
    {
        using var temp = new TempDir();

        StateDirectorySecurity
            .IsTrusted(Path.Combine(temp.Path, "does-not-exist"))
            .Should().BeFalse();
    }

    [WindowsFact("Windows ACL APIs")]
    public void IsTrusted_is_true_for_a_real_admin_only_directory()
    {
        // Arrange — %WINDIR%\System32 is owned by NT SERVICE\TrustedInstaller and
        // grants write-class rights only to TrustedInstaller / SYSTEM /
        // Administrators. Without a positive case the entire suite would be
        // satisfied by `IsTrusted(_) => false`, and that regression would kill
        // machine-scope uninstall on every machine while CI stayed green (R6).
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        Directory.Exists(system32).Should().BeTrue(
            "the positive case needs a real admin-only directory to assert against");

        // Act
        var trusted = StateDirectorySecurity.IsTrusted(system32);

        // Assert
        trusted.Should().BeTrue(
            "a TrustedInstaller-owned directory that no non-administrator can write " +
            "is exactly what 'trusted' means");
    }

    [WindowsFact("Windows ACL APIs")]
    public void IsTrusted_is_false_for_an_admin_owned_but_user_writable_directory()
    {
        // Arrange — THE R1 case. %ProgramData% is owned by NT AUTHORITY\SYSTEM, so
        // an owner-only check answers "trusted", yet its DACL carries
        // BUILTIN\Users:(CI)(WD,AD,WEA,WA) — any unprivileged user can create files
        // and directories in it.
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        Directory.Exists(programData).Should().BeTrue();

        // Act
        var trusted = StateDirectorySecurity.IsTrusted(programData);

        // Assert
        trusted.Should().BeFalse(
            "admin-OWNED is not admin-only-WRITABLE: %ProgramData% grants BUILTIN\\Users " +
            "write-class rights, which is the whole of register row R1");
    }

    [WindowsFact("Windows ACL APIs")]
    public void IsAdminOnlyWritable_is_false_for_a_user_writable_container()
    {
        // Arrange — %TEMP% grants the interactive user FullControl, so anything
        // sited under it can be renamed or swapped by an unprivileged process.
        // Consumed by S2 (SYSTEM-level step targets) and S3 (staging).
        using var temp = new TempDir();
        var dir = Path.Combine(temp.Path, "staging");
        Directory.CreateDirectory(dir);

        // Act
        var adminOnly = StateDirectorySecurity.IsAdminOnlyWritable(dir);

        // Assert
        adminOnly.Should().BeFalse(
            "a directory under the user's own %TEMP% is owned and writable by that " +
            "user, so it is not a safe home for elevated-lifetime state");
    }

    [WindowsFact("Windows ACL APIs")]
    public void IsAdminOnlyWritable_fails_closed_when_neither_the_path_nor_its_container_exists()
    {
        // Arrange — the path is not a directory, so the container is examined; that
        // does not exist either, and a predicate that cannot look must answer false.
        using var temp = new TempDir();
        var orphan = Path.Combine(temp.Path, "no-such-container", "child");

        // Act
        var adminOnly = StateDirectorySecurity.IsAdminOnlyWritable(orphan);

        // Assert
        adminOnly.Should().BeFalse(
            "with no directory to read an ACL from, the only safe answer is 'not admin-only'");
    }

    [WindowsFact("Windows ACL APIs")]
    public void IsAdminOnlyWritable_is_true_for_System32()
    {
        // Arrange
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        Directory.Exists(system32).Should().BeTrue(
            "the positive case needs a real admin-only directory to assert against");

        // Act
        var adminOnly = StateDirectorySecurity.IsAdminOnlyWritable(system32);

        // Assert
        adminOnly.Should().BeTrue(
            "only SYSTEM, Administrators and TrustedInstaller hold write-class " +
            "rights on %WINDIR%\\System32");
    }

    [WindowsFact("Windows ACL APIs")]
    public void IsAdminOnlyWritable_is_true_for_Program_Files()
    {
        // Arrange — machine-scope installs land HERE (ScopeLayout.InstallRoot), and
        // %ProgramFiles% is owned by NT SERVICE\TrustedInstaller. If TrustedInstaller
        // were not a trusted owner this predicate would refuse every legitimate
        // machine install — an anchoring bug worse than the hole it closes. This test
        // pins TrustedInstaller into the trusted-owner set.
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        Directory.Exists(programFiles).Should().BeTrue();

        // Act
        var adminOnly = StateDirectorySecurity.IsAdminOnlyWritable(programFiles);

        // Assert
        adminOnly.Should().BeTrue(
            "%ProgramFiles% is TrustedInstaller-owned and admin-only writable; S2 gates " +
            "privileged step targets on this answer");
    }

    [WindowsFact("Windows ACL APIs")]
    public void IsAdminOnlyWritable_inspects_the_directory_itself_not_only_its_parent()
    {
        // Arrange — %WINDIR% is admin-only, but its parent (the volume root) grants
        // NT AUTHORITY\Authenticated Users:(M). A predicate that examined only the
        // container would answer false for %WINDIR% and true for anything sited
        // directly under an admin-only parent, which is backwards.
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var volumeRoot = Path.GetPathRoot(windows);
        Directory.Exists(windows).Should().BeTrue();
        volumeRoot.Should().NotBeNullOrEmpty();

        // Act
        var windowsIsAdminOnly = StateDirectorySecurity.IsAdminOnlyWritable(windows);
        var rootIsAdminOnly = StateDirectorySecurity.IsAdminOnlyWritable(volumeRoot!);

        // Assert
        windowsIsAdminOnly.Should().BeTrue(
            "%WINDIR%'s OWN acl is admin-only, even though its parent is not");
        rootIsAdminOnly.Should().BeFalse(
            "the volume root grants Authenticated Users Modify");
    }

    [WindowsFact("Windows ACL APIs")]
    public void IsAdminOnlyWritable_is_false_for_a_user_writable_directory_under_an_admin_only_parent()
    {
        // Arrange — %WINDIR%\Tracing carries BUILTIN\Users:(RX,W) and (R,W) on the
        // directory ITSELF while its parent %WINDIR% is admin-only. This is the
        // counterexample that proves the predicate reads the target's own ACL: a
        // staging directory or SYSTEM step target sited here would pass a
        // parent-only check while every file in it stays replaceable by any user.
        var tracing = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Tracing");
        Directory.Exists(tracing).Should().BeTrue(
            "the counterexample needs the stock %WINDIR%\\Tracing directory to exist");

        // Act
        var adminOnly = StateDirectorySecurity.IsAdminOnlyWritable(tracing);

        // Assert
        adminOnly.Should().BeFalse(
            "BUILTIN\\Users holds write-class rights on %WINDIR%\\Tracing itself");
    }

    [WindowsFact("Windows ACL APIs")]
    public void IsAdminOnlyWritable_is_false_for_ProgramData()
    {
        // Arrange — the directory the machine-scope state root lives under.
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        Directory.Exists(programData).Should().BeTrue();

        // Act
        var adminOnly = StateDirectorySecurity.IsAdminOnlyWritable(programData);

        // Assert
        adminOnly.Should().BeFalse(
            "%ProgramData% grants BUILTIN\\Users create-files/create-directories, which " +
            "is precisely what makes a bare CreateDirectory under it exploitable");
    }

    [WindowsFact("Windows ACL APIs")]
    public void CreateHardened_produces_a_protected_admin_only_dacl()
    {
        // Arrange
        using var temp = new TempDir();
        var dir = Path.Combine(temp.Path, "hardened");

        try
        {
            // Act
            StateDirectorySecurity.CreateHardened(dir);

            // Assert
            Directory.Exists(dir).Should().BeTrue();
            var security = new DirectoryInfo(dir).GetAccessControl(AccessControlSections.Access);
            security.AreAccessRulesProtected.Should().BeTrue(
                "SetAccessRuleProtection(true, false) must DISCARD the permissive inherited " +
                "ACEs rather than merge them — merging them is the whole bug");

            DescribeRules(security).Should().BeEquivalentTo(new[]
            {
                $"{LocalSystem.Value}|{FileSystemRights.FullControl}|Allow",
                $"{Administrators.Value}|{FileSystemRights.FullControl}|Allow",
                // Windows persists the implied SYNCHRONIZE bit alongside ReadAndExecute;
                // FullControl already contains it, which is why only this line names it.
                $"{Users.Value}|{FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize}|Allow",
            });

            // Ownership is not asserted here: it depends on whether the host is
            // elevated, which CreateHardened_repairs_ownership_when_it_can… covers.
        }
        finally
        {
            Unharden(dir);
        }
    }

    [WindowsFact("Windows ACL APIs")]
    public void CreateHardened_repairs_an_existing_directory_that_grants_a_non_admin_write()
    {
        // Arrange — the CR-3 shape: a state directory that already exists and carries
        // a write-class grant for a non-administrator. Pre-fix installs are in exactly
        // this state (Administrators-owned, still inheriting %ProgramData%'s
        // BUILTIN\Users:(WD,AD)), and the old code returned without touching it.
        using var temp = new TempDir();
        var dir = Path.Combine(temp.Path, "legacy-state");
        Directory.CreateDirectory(dir);

        var me = WindowsIdentity.GetCurrent().User;
        me.Should().NotBeNull();

        try
        {
            var permissive = new DirectoryInfo(dir).GetAccessControl();
            permissive.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            permissive.AddAccessRule(new FileSystemAccessRule(
                me!,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            new DirectoryInfo(dir).SetAccessControl(permissive);

            DescribeRules(new DirectoryInfo(dir).GetAccessControl(AccessControlSections.Access))
                .Should().Contain($"{me!.Value}|{FileSystemRights.FullControl}|Allow",
                    "the fixture must really be permissive, or the repair proves nothing");

            var progress = new CapturingProgress();

            // Act — no throw: Ruling 2 says CreateHardened repairs rather than refuses.
            StateDirectorySecurity.CreateHardened(dir, progress);

            // Assert
            var after = new DirectoryInfo(dir).GetAccessControl(AccessControlSections.Access);
            after.AreAccessRulesProtected.Should().BeTrue();
            DescribeRules(after).Should().BeEquivalentTo(new[]
            {
                $"{LocalSystem.Value}|{FileSystemRights.FullControl}|Allow",
                $"{Administrators.Value}|{FileSystemRights.FullControl}|Allow",
                // Windows persists the implied SYNCHRONIZE bit alongside ReadAndExecute;
                // FullControl already contains it, which is why only this line names it.
                $"{Users.Value}|{FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize}|Allow",
            },
            "the repair must replace the DACL wholesale, dropping the non-admin write grant");

            progress.Messages.Should().Contain(
                m => m.Contains("repaired", StringComparison.OrdinalIgnoreCase),
                "a silent repair of a security boundary leaves no trail for an incident responder");
        }
        finally
        {
            Unharden(dir);
        }
    }

    /// <summary>
    /// The repair path also hands ownership to <c>BUILTIN\Administrators</c>, because
    /// an owner keeps implicit <c>WRITE_DAC</c> and <c>IsTrusted</c> demands a trusted
    /// owner. Only an ELEVATED caller can assign that SID, so which branch runs depends
    /// on the host: a developer box is normally unelevated, GitHub's
    /// <c>windows-latest</c> runners execute elevated. Both are asserted, and the case
    /// is derived from the OBSERVED owner rather than from an environment variable —
    /// neither branch is vacuous and either can fail for a real defect.
    /// </summary>
    [WindowsFact("Windows ACL APIs")]
    public void CreateHardened_repairs_ownership_when_it_can_and_keeps_the_dacl_when_it_cannot()
    {
        // Arrange
        using var temp = new TempDir();
        var dir = Path.Combine(temp.Path, "attacker-owned");
        Directory.CreateDirectory(dir);
        try
        {
            var me = WindowsIdentity.GetCurrent().User;
            me.Should().NotBeNull();

            // Holds on both host kinds: %TEMP% grants its user FullControl, which is
            // inherited here, so the fixture is untrusted however it is owned and the
            // repair path is the one that runs.
            StateDirectorySecurity.IsTrusted(dir).Should().BeFalse(
                "the fixture must need repairing, or this test exercises nothing");

            var progress = new CapturingProgress();

            // Act — must succeed whether or not the ownership half can.
            StateDirectorySecurity.CreateHardened(dir, progress);

            // Assert (1) — the invariant that holds on EVERY host: the DACL repair
            // happened and is intact, whatever the ownership attempt did.
            var after = new DirectoryInfo(dir).GetAccessControl(AccessControlSections.Access);
            after.AreAccessRulesProtected.Should().BeTrue(
                "the DACL repair is the load-bearing half and must never be skipped or " +
                "rolled back by the outcome of the ownership attempt");
            DescribeRules(after).Should().BeEquivalentTo(new[]
            {
                $"{LocalSystem.Value}|{FileSystemRights.FullControl}|Allow",
                $"{Administrators.Value}|{FileSystemRights.FullControl}|Allow",
                // Windows persists the implied SYNCHRONIZE bit alongside ReadAndExecute;
                // FullControl already contains it, which is why only this line names it.
                $"{Users.Value}|{FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize}|Allow",
            });

            // Assert (2) — which branch ran, decided by the actual owner on disk. The
            // ownership repair assigns exactly BUILTIN\Administrators, so this is an
            // exact discriminator, not a guess.
            var owner = OwnerOf(dir);
            if (owner is not null && Administrators.Equals(owner))
            {
                // Elevated host. This is the branch a developer box cannot reach, and
                // it is the one that proves the round-3 fix actually completes: after a
                // successful repair the directory must pass the R1 gate end to end.
                StateDirectorySecurity.IsTrusted(dir).Should().BeTrue(
                    "an elevated CreateHardened must leave a directory that is trusted — " +
                    "a repaired DACL with an untrusted owner would still refuse its own state");
                progress.Messages.Should().Contain(
                    m => m.Contains("took ownership", StringComparison.OrdinalIgnoreCase),
                    "a successful ownership repair must be recorded");
            }
            else
            {
                // Unelevated host: the assignment cannot succeed, so the creator stays
                // the owner and the directory stays untrusted — fail-closed, and said
                // out loud rather than silently degraded.
                owner.Should().Be(me!,
                    "an unelevated token cannot assign BUILTIN\\Administrators, so the " +
                    "creator must still own it");
                StateDirectorySecurity.IsTrusted(dir).Should().BeFalse(
                    "an untrusted owner must keep the directory untrusted no matter how " +
                    "clean the DACL is");
                progress.Messages.Should().Contain(
                    m => m.Contains("could not take ownership", StringComparison.OrdinalIgnoreCase),
                    "a skipped ownership fix leaves the directory untrusted, so it must be said out loud");
            }
        }
        finally
        {
            Unharden(dir);
        }
    }

    /// <summary>
    /// Gate G1 attack #1, expressed as a test: an unprivileged user pre-creates
    /// <c>%ProgramData%\Sigil\&lt;AppId&gt;</c> with a plain
    /// <see cref="Directory.CreateDirectory(string)"/> and plants a well-formed
    /// <c>uninstall.json</c> in it. The elevated uninstall must refuse to replay it.
    /// Runs unelevated on purpose — that IS the attacker's position.
    /// </summary>
    [WindowsFact("Windows ACL APIs")]
    public void Machine_scope_state_planted_by_an_unprivileged_user_is_refused()
    {
        var appId = "sigil.r1." + Guid.NewGuid().ToString("N");
        var machineDir = UninstallStateStore.DirectoryFor(appId, InstallScope.Machine);
        var userDir = UninstallStateStore.DirectoryFor(appId, InstallScope.User);

        try
        {
            // Arrange (1) — a genuinely well-formed payload with one record, produced
            // by the real store so nothing about the wire shape is guessed.
            var journal = new RollbackJournal();
            journal.Append(new RollbackRecord.RemoveDirectory(
                Path.Combine(Path.GetTempPath(), "sigil-r1-" + appId)));
            UninstallStateStore.Save(appId, journal, InstallScope.User);
            var payload = File.ReadAllText(UninstallStateStore.PathFor(appId, InstallScope.User));
            payload.Should().Contain("remove_directory");

            // The user-scope copy must be gone, or it could rescue the load and the
            // machine-scope result would prove nothing.
            UninstallStateStore.Delete(appId, InstallScope.User);
            File.Exists(UninstallStateStore.PathFor(appId, InstallScope.User)).Should().BeFalse();

            // Arrange (2) — plant it under %ProgramData% as the unprivileged current
            // user, with a bare CreateDirectory, exactly as the attacker would.
            var machinePath = UninstallStateStore.PathFor(appId, InstallScope.Machine);
            Directory.CreateDirectory(machineDir);
            File.WriteAllText(machinePath, payload);
            File.Exists(machinePath).Should().BeTrue(
                "if %ProgramData% is not writable in this session the attack fixture is " +
                "not real and this test must be reported as inconclusive, not passing");
            StateDirectorySecurity.IsTrusted(machineDir).Should().BeFalse(
                "the planted directory must really be attacker-owned/writable");

            // Arrange (3) — the FILE is planted too, not just the directory. That is the
            // half of R1 that survives a hardened container: File.WriteAllText truncates
            // in place, so the planted file keeps its owner and its explicit ACEs even
            // after an elevated installer has repaired the directory around it.
            //
            // The ACE below is what makes this fixture host-INDEPENDENT. Unelevated, the
            // planted file is already untrusted because this user owns it. Elevated —
            // which is how GitHub's windows-latest runners execute — a file created under
            // %ProgramData% is Administrators-owned (its Users write ACE is (CI)-only and
            // so does not reach files), and would read as trusted. Granting BUILTIN\Users
            // FullControl puts a non-administrator write-class right on the file itself,
            // which fails the DACL half of the check on EITHER kind of host. Without it
            // the assertion below could not fail on CI, and the read half of the fix
            // could be deleted with the suite still green.
            var planted = new FileInfo(machinePath).GetAccessControl();
            planted.AddAccessRule(new FileSystemAccessRule(
                Users, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(machinePath).SetAccessControl(planted);

            StateDirectorySecurity.IsTrustedFile(machinePath).Should().BeFalse(
                "the fixture must really be an attacker-writable file on every host, or " +
                "the refusal assertion below would be unfalsifiable");

            // Act
            var progress = new CapturingProgress();
            var loaded = UninstallStateStore.TryLoad(appId, InstallScope.Machine, progress);

            // Assert — refused.
            loaded.Should().BeNull(
                "machine-scope state from a directory an unprivileged user could have " +
                "authored must never be replayed by an elevated uninstall (R1)");

            // …and refused for the right reason, not incidentally null: the store
            // reports a refusal distinct from an absence, and says so on the sink.
            var reason = UninstallStateStore.Load(appId, InstallScope.Machine).RefusalReason;
            reason.Should().NotBeNullOrEmpty(
                "a refusal must be distinguishable from 'no prior install'");
            reason.Should().Contain("the state directory",
                "the planted container must be named in the refusal");
            progress.Messages.Should().Contain(
                m => m.Contains("refusing state in", StringComparison.Ordinal),
                "a silent refusal reads as 'no prior install' and would mask the attack");

            // Unconditional, on every host: the refusal must name the FILE as well.
            // Deleting the file term from the load site's check would leave the reason
            // reading "the state directory" alone and fail here — including on an
            // elevated CI runner, which is the point. Trusting a file because its
            // directory is trusted is the second half of R1.
            reason.Should().Contain("the state file",
                "a hardened directory does not make an attacker-owned file inside it safe");

            // Control — the very same bytes DO load from user scope, so the null above
            // is the R1 refusal and not a malformed fixture or a missing file.
            Directory.CreateDirectory(userDir);
            File.WriteAllText(UninstallStateStore.PathFor(appId, InstallScope.User), payload);
            var control = UninstallStateStore.TryLoad(appId, InstallScope.User);
            control.Should().NotBeNull();
            control!.Journal.Records.Should().HaveCount(1);
        }
        finally
        {
            UninstallStateStore.Delete(appId, InstallScope.User);

            // The planted directory carries the fixture's own ACEs and lives in
            // %ProgramData%, which is shared across runs — leaving it behind would
            // litter and eventually collide. Re-grant as owner first (the same
            // technique the hardened-directory tests use) so the delete cannot be
            // refused by the ACL the fixture itself stamped on.
            Unharden(machineDir);
#pragma warning disable CA1031 // Best-effort cleanup of the planted attack fixture.
            try
            {
                if (Directory.Exists(machineDir))
                {
                    Directory.Delete(machineDir, recursive: true);
                }
            }
            catch
            {
                // Best-effort.
            }
#pragma warning restore CA1031
        }
    }

    [WindowsFact("Windows ACL APIs")]
    public void IsTrustedFile_is_true_for_a_real_admin_only_file()
    {
        // Arrange — %WINDIR%\System32\kernel32.dll is owned by NT SERVICE\TrustedInstaller
        // and grants nobody a write-class right but TrustedInstaller itself.
        var kernel32 = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "kernel32.dll");
        File.Exists(kernel32).Should().BeTrue(
            "the positive case needs a real admin-only file to assert against");

        // Act / Assert
        StateDirectorySecurity.IsTrustedFile(kernel32).Should().BeTrue(
            "a TrustedInstaller-owned file that no non-administrator can write is trusted");
    }

    [WindowsFact("Windows ACL APIs")]
    public void IsTrustedFile_is_false_for_a_file_the_current_user_owns()
    {
        // Arrange — the shape of a planted uninstall.json: created by an unprivileged
        // user, who therefore owns it and keeps implicit WRITE_DAC on it forever.
        using var temp = new TempDir();
        var file = Path.Combine(temp.Path, "uninstall.json");
        File.WriteAllText(file, "{}");

        // Act / Assert
        StateDirectorySecurity.IsTrustedFile(file).Should().BeFalse(
            "a file owned by a non-administrator can be re-permissioned by that user at " +
            "any time, so its contents must never be replayed with elevation");
    }

    [WindowsFact("Windows ACL APIs")]
    public void IsTrustedFile_fails_closed_on_a_missing_file()
    {
        using var temp = new TempDir();

        StateDirectorySecurity
            .IsTrustedFile(Path.Combine(temp.Path, "does-not-exist.json"))
            .Should().BeFalse(
                "fail closed — and note this never refuses a first install, because the " +
                "load path only consults it after File.Exists has already succeeded");
    }

    /// <summary>
    /// R1, write half: <c>File.WriteAllText</c> truncates in place, so a pre-existing
    /// file survives the write with its owner and explicit ACEs intact. <c>Save</c>
    /// must replace the file instead. Asserted through the observable consequence —
    /// a protected DACL planted on the old file cannot survive — which holds on an
    /// elevated and an unelevated host alike. User scope is used because that is the
    /// scope an unelevated test can actually drive; the replacement code path is the
    /// same one machine scope takes.
    /// </summary>
    [WindowsFact("Windows ACL APIs")]
    public void Save_replaces_a_pre_existing_state_file_instead_of_truncating_it()
    {
        var appId = "sigil.r1file." + Guid.NewGuid().ToString("N");
        var dir = UninstallStateStore.DirectoryFor(appId, InstallScope.User);
        var path = UninstallStateStore.PathFor(appId, InstallScope.User);

        try
        {
            // Arrange — a pre-existing state file carrying a PROTECTED DACL of its own,
            // standing in for the attacker's explicit ACEs. Nothing of it may survive.
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, "{}");

            var me = WindowsIdentity.GetCurrent().User;
            me.Should().NotBeNull();

            var planted = new FileInfo(path).GetAccessControl();
            planted.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            planted.AddAccessRule(new FileSystemAccessRule(
                me!, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(planted);

            new FileInfo(path).GetAccessControl(AccessControlSections.Access)
                .AreAccessRulesProtected.Should().BeTrue(
                    "the fixture must really carry a DACL of its own, or the test proves nothing");

            // Act
            var journal = new RollbackJournal();
            journal.Append(new RollbackRecord.RemoveDirectory(
                Path.Combine(Path.GetTempPath(), "sigil-r1file-" + appId)));
            UninstallStateStore.Save(appId, journal, InstallScope.User);

            // Assert — the file that exists now is a NEW file that inherited the
            // directory's DACL, not the planted one truncated in place. If Save still
            // called File.WriteAllText over the target, the protected DACL below would
            // still be protected and this assertion would fail.
            new FileInfo(path).GetAccessControl(AccessControlSections.Access)
                .AreAccessRulesProtected.Should().BeFalse(
                    "Save must create a fresh file and move it over the target; truncating " +
                    "in place preserves the previous owner and DACL, which is register row R1");

            // …and the state is still readable, i.e. the replacement is not a regression.
            UninstallStateStore.TryLoad(appId, InstallScope.User)!.Journal.Records
                .Should().HaveCount(1);

            // No staging file may be left behind in the state directory.
            Directory.GetFiles(dir).Should().ContainSingle()
                .Which.Should().EndWith("uninstall.json");
        }
        finally
        {
            UninstallStateStore.Delete(appId, InstallScope.User);
        }
    }

    /// <summary>
    /// Re-grant write access so <see cref="TempDir"/> can actually delete a hardened
    /// directory. <see cref="StateDirectorySecurity.CreateHardened"/> leaves
    /// <c>BUILTIN\Users</c> with ReadAndExecute only, so an unelevated caller cannot
    /// remove it or its contents without re-permissioning it first (it still can as the
    /// owner) — without this, every run would litter the temp tree. Best-effort: a
    /// cleanup failure must not mask the assertion that ran before it.
    /// </summary>
    private static void Unharden(string directory)
    {
#pragma warning disable CA1031 // Best-effort test cleanup.
        try
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            var security = new DirectoryInfo(directory).GetAccessControl();
            security.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);
            var me = WindowsIdentity.GetCurrent().User;
            if (me is not null)
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    me,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
            }
            new DirectoryInfo(directory).SetAccessControl(security);
        }
        catch
        {
            // Best-effort.
        }
#pragma warning restore CA1031
    }

    /// <summary>The directory's owner SID, or <c>null</c> if it cannot be read.</summary>
    private static SecurityIdentifier? OwnerOf(string directory) =>
        new DirectoryInfo(directory)
            .GetAccessControl(AccessControlSections.Owner)
            .GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;

    /// <summary>
    /// Renders a DACL as <c>sid|rights|type</c> strings so an assertion can name the
    /// exact ACE set rather than counting rules.
    /// </summary>
    private static List<string> DescribeRules(DirectorySecurity security)
    {
        var described = new List<string>();
        foreach (FileSystemAccessRule rule in security.GetAccessRules(
            includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            described.Add(
                $"{rule.IdentityReference.Value}|{rule.FileSystemRights}|{rule.AccessControlType}");
        }
        return described;
    }

    /// <summary>Captures the messages reported on an <see cref="IProgress{T}"/> sink.</summary>
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
