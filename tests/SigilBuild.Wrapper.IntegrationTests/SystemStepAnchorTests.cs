namespace SigilBuild.Wrapper.IntegrationTests;

using System;
using System.IO;
using System.Runtime.Versioning;
using FluentAssertions;
using SigilBuild.Wrapper.Engine;

/// <summary>
/// Register row R67: the unelevated, non-VM half of the P11 system-step fixture —
/// everything about <see cref="SystemStepInstallDir"/>'s anchor that can be proved
/// without admin rights, so the VM matrix is no longer the <em>first</em> place the
/// anchoring is exercised.
/// </summary>
/// <remarks>
/// <para>
/// The <c>vm (p11 system steps)</c> leg failed on its first ever run because the
/// two legs it carries had never executed anywhere: they were written against
/// <see cref="StepContext.Empty"/> and system-binary targets, both of which lane
/// S2's <see cref="PrivilegedTargetGuard"/> refuses. These cases run on every
/// Windows PR build and are pure computation — no task, no service, no COM
/// registration, no write outside the process — so a future regression in the
/// fixture's anchor is caught before the matrix runs, not by it.
/// </para>
/// <para>
/// What is <b>not</b> provable here, honestly stated: the elevated legs create
/// their <c>install_dir</c> under <c>%ProgramFiles%</c>, and an unelevated process
/// cannot create that directory. The guard's ACL arm inspects the directory the
/// target sits in, so with the per-run leaf absent the arm answers "not admin-only"
/// on a technicality (<see cref="StateDirectorySecurity.IsAdminOnlyWritable"/>
/// fails closed for a directory that does not exist). The two arms are therefore
/// pinned separately below — containment against the fixture's own resolved anchor,
/// and the ACL predicate against the machine scope root that the created directory
/// inherits from — plus one full-guard accept case against an existing admin-only
/// directory under that same root.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public class SystemStepAnchorTests
{
    private static string ProgramFiles =>
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    [WindowsFact("Windows scope roots")]
    public void The_fixture_anchor_resolves_through_the_real_install_dir_resolver()
    {
        // ResolveAnchor goes through CommandLineParser + StepContext.From +
        // InstallDirResolver, which is where R3's scope-root containment lives: a
        // machine-scope /D= outside %ProgramFiles% throws InstallDirRejectedException.
        // Reaching an assertion at all therefore proves the fixture's anchor is one
        // production would accept.
        var (dir, context) = SystemStepInstallDir.ResolveAnchor();

        context.InstallDir.Should().Be(
            dir, "the /D= value must survive resolution as the run's install_dir");
        PathContainment.IsUnder(ProgramFiles, dir).Should().BeTrue(
            "a machine-scope install_dir must sit under the machine install root");
    }

    [WindowsFact("Windows scope roots")]
    public void The_fixture_target_names_are_contained_and_the_old_system_targets_are_not()
    {
        var (dir, context) = SystemStepInstallDir.ResolveAnchor();
        var anchor = context.InstallDir!;

        // The two targets the elevated legs use, after the copy-into-install_dir.
        PathContainment.IsUnderWithoutTraversal(anchor, Path.Combine(dir, "SigilItHeartbeat.exe"))
            .Should().BeTrue("scheduled_task_create's program is copied into install_dir");
        PathContainment.IsUnderWithoutTraversal(anchor, Path.Combine(dir, "sigilcomprobe.dll"))
            .Should().BeTrue("com_register's DLL is copied into install_dir");

        // The two targets the legs used to name — the reason the first VM run failed.
        PathContainment.IsUnderWithoutTraversal(anchor, Path.Combine(Environment.SystemDirectory, "cmd.exe"))
            .Should().BeFalse("a System32 binary is outside any install_dir");
        PathContainment.IsUnderWithoutTraversal(anchor, Path.Combine(Environment.SystemDirectory, "kernel32.dll"))
            .Should().BeFalse("a System32 DLL is outside any install_dir");
    }

    [WindowsFact("Windows ACL APIs")]
    public void The_machine_install_root_satisfies_the_guards_acl_arm()
    {
        // The fixture's directory is created under %ProgramFiles% precisely so it
        // inherits an admin-only DACL. If this host's %ProgramFiles% is writable by
        // a non-administrator, the elevated legs would fail on the ACL arm and the
        // cause would look like a product bug — so state the environmental
        // precondition here instead.
        StateDirectorySecurity.IsAdminOnlyWritable(ProgramFiles).Should().BeTrue(
            $"'{ProgramFiles}' must be admin-only writable — it is the machine scope root every " +
            "privileged step target is required to descend from");
    }

    [WindowsFact("Windows ACL APIs")]
    public void The_guard_accepts_a_program_files_rooted_target_in_an_existing_admin_only_directory()
    {
        // The accept side of the full guard, using an existing admin-only directory
        // under the same root as a stand-in for the per-run install_dir the elevated
        // fixture creates. Asserted against the PrivilegedTargetGuard seam rather
        // than by running a step, so nothing here can create a task or load a DLL.
        var anchor = Path.Combine(ProgramFiles, "Common Files");
        StateDirectorySecurity.IsAdminOnlyWritable(anchor).Should().BeTrue(
            $"'{anchor}' must be admin-only writable for this accept case to mean anything");

        PrivilegedTargetGuard.Check(
                "scheduled_task_create", "program", anchor, Path.Combine(anchor, "SigilItHeartbeat.exe"))
            .Should().BeNull();
        PrivilegedTargetGuard.Check(
                "com_register", "path", anchor, Path.Combine(anchor, "sigilcomprobe.dll"))
            .Should().BeNull();
    }

    [WindowsFact("Windows ACL APIs")]
    public void The_two_refusals_that_failed_the_first_vm_run_still_fire()
    {
        // Verbatim the two failures from run 34361541578, pinned so the fixture can
        // never quietly regress to either shape.
        var systemBinary = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var systemDll = Path.Combine(Environment.SystemDirectory, "kernel32.dll");
        var (_, context) = SystemStepInstallDir.ResolveAnchor();

        PrivilegedTargetGuard.Check("scheduled_task_create", "program", installDir: null, systemBinary)
            .Should().Contain("no resolved install_dir",
                "a run with no anchor is refused rather than waved through");
        PrivilegedTargetGuard.Check("com_register", "path", installDir: null, systemDll)
            .Should().Contain("no resolved install_dir");

        PrivilegedTargetGuard.Check("scheduled_task_create", "program", context.InstallDir, systemBinary)
            .Should().Contain("does not resolve inside install_dir",
                "an anchored run still refuses a target outside the install directory");
        PrivilegedTargetGuard.Check("com_register", "path", context.InstallDir, systemDll)
            .Should().Contain("does not resolve inside install_dir");
    }
}
