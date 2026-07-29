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

        // In production CreateHardened runs only for machine scope, which only
        // happens elevated, so the owner is Administrators/SYSTEM there and the
        // directory does pass IsTrusted. Unelevated it cannot — the owner is this
        // user — so asserting the round trip here would assert a falsehood.
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

    [WindowsFact("Windows ACL APIs")]
    public void CreateHardened_keeps_the_dacl_repair_when_taking_ownership_fails()
    {
        // Arrange — the repair path also hands ownership to BUILTIN\Administrators,
        // because an owner keeps implicit WRITE_DAC and IsTrusted demands a trusted
        // owner. Only an ELEVATED caller can assign that SID, and this session is not
        // elevated, so this test exercises the best-effort failure branch: the one that
        // must not take the DACL repair down with it.
        using var temp = new TempDir();
        var dir = Path.Combine(temp.Path, "attacker-owned");
        Directory.CreateDirectory(dir);

        var me = WindowsIdentity.GetCurrent().User;
        me.Should().NotBeNull();
        OwnerOf(dir).Should().Be(me!, "an unelevated process owns what it creates");

        var progress = new CapturingProgress();

        // Act — must not throw even though the ownership half cannot succeed here.
        StateDirectorySecurity.CreateHardened(dir, progress);

        // Assert — the ownership attempt really did fail (otherwise this test would be
        // asserting the wrong branch), and the DACL repair survived it intact.
        OwnerOf(dir).Should().Be(me!,
            "an unelevated token cannot assign BUILTIN\\Administrators as owner — this " +
            "test is only meaningful while that stays true");

        var after = new DirectoryInfo(dir).GetAccessControl(AccessControlSections.Access);
        after.AreAccessRulesProtected.Should().BeTrue(
            "a failed ownership fix must not roll back or skip the DACL repair");
        DescribeRules(after).Should().BeEquivalentTo(new[]
        {
            $"{LocalSystem.Value}|{FileSystemRights.FullControl}|Allow",
            $"{Administrators.Value}|{FileSystemRights.FullControl}|Allow",
            // Windows persists the implied SYNCHRONIZE bit alongside ReadAndExecute;
            // FullControl already contains it, which is why only this line names it.
            $"{Users.Value}|{FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize}|Allow",
        });

        progress.Messages.Should().Contain(
            m => m.Contains("could not take ownership", StringComparison.OrdinalIgnoreCase),
            "a skipped ownership fix leaves the directory untrusted, so it must be said out loud");

        // NOT asserted, because it cannot be from here: that an ELEVATED caller ends up
        // with owner == BUILTIN\Administrators and a directory that then passes
        // IsTrusted. That path needs an elevated run (gate G1).
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
            Directory.CreateDirectory(machineDir);
            File.WriteAllText(UninstallStateStore.PathFor(appId, InstallScope.Machine), payload);
            File.Exists(UninstallStateStore.PathFor(appId, InstallScope.Machine)).Should().BeTrue(
                "if %ProgramData% is not writable in this session the attack fixture is " +
                "not real and this test must be reported as inconclusive, not passing");
            StateDirectorySecurity.IsTrusted(machineDir).Should().BeFalse(
                "the planted directory must really be attacker-owned/writable");

            // Act
            var progress = new CapturingProgress();
            var loaded = UninstallStateStore.TryLoad(appId, InstallScope.Machine, progress);

            // Assert — refused.
            loaded.Should().BeNull(
                "machine-scope state from a directory an unprivileged user could have " +
                "authored must never be replayed by an elevated uninstall (R1)");

            // …and refused for the right reason, not incidentally null: the store
            // reports a refusal distinct from an absence, and says so on the sink.
            UninstallStateStore.Load(appId, InstallScope.Machine).RefusalReason
                .Should().NotBeNullOrEmpty(
                    "a refusal must be distinguishable from 'no prior install'");
            progress.Messages.Should().Contain(
                m => m.Contains("refusing state in", StringComparison.Ordinal),
                "a silent refusal reads as 'no prior install' and would mask the attack");

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
