namespace SigilBuild.Wrapper.Tests.Engine;

using System;
using System.IO;
using System.Runtime.Versioning;
using FluentAssertions;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Tests.Helpers;

/// <summary>
/// The pure seam behind register rows R3 and R9. Exercising the predicate
/// directly is what lets the accept side be proved at all: running one of the
/// four privileged steps to completion would create a real scheduled task,
/// service, COM registration or firewall rule on an elevated runner, so no test
/// does that — the steps are only ever driven down their refusal paths.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PrivilegedTargetGuardTests
{
    private const string JunctionPrefix = "sigil-s23-junction-";

    /// <summary>An existing, admin-only-writable machine directory.</summary>
    private static string MachineAnchor =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Common Files");

    [WindowsFact("Windows ACL APIs")]
    public void Accepts_a_descendant_of_an_admin_only_install_dir()
    {
        var anchor = MachineAnchor;
        StateDirectorySecurity.IsAdminOnlyWritable(Path.Combine(anchor, "probe.exe"))
            .Should().BeTrue($"'{anchor}' must be admin-only writable or this case proves nothing");

        PrivilegedTargetGuard.Check("scheduled_task_create", "program", anchor, Path.Combine(anchor, "app.exe"))
            .Should().BeNull();
    }

    [WindowsFact("Windows ACL APIs")]
    public void Accepts_a_descendant_when_the_root_carries_a_trailing_separator()
    {
        // /D=C:\...\ survives Path.GetFullPath with its trailing separator, so
        // ctx.InstallDir can carry one. Both PathContainment members canonicalize
        // via Path.TrimEndingDirectorySeparator; without that the upward walk
        // never meets the anchor and a genuine descendant is refused.
        var anchor = MachineAnchor;

        PrivilegedTargetGuard.Check(
                "scheduled_task_create",
                "program",
                anchor + Path.DirectorySeparatorChar,
                Path.Combine(anchor, "app.exe"))
            .Should().BeNull();
    }

    [WindowsFact("Windows ACL APIs")]
    public void Refuses_a_target_outside_the_install_dir_naming_the_containment_check()
    {
        var message = PrivilegedTargetGuard.Check(
            "scheduled_task_create", "program", MachineAnchor, @"C:\Users\Public\evil.exe");

        message.Should().NotBeNull();
        message.Should().Contain("install_dir");
        message.Should().Contain("scheduled_task_create");
        message.Should().Contain("program");
    }

    [WindowsFact("Windows ACL APIs")]
    public void Refuses_a_contained_target_whose_directory_a_non_administrator_can_write()
    {
        // The check that actually stops R3: containment alone is satisfied by any
        // path under install_dir, and a per-user install root is always
        // user-writable. %ProgramData% is the register's own R1 example.
        using var installDir = new TempDir();

        var message = PrivilegedTargetGuard.Check(
            "service_install", "binary_path", installDir.Path, Path.Combine(installDir.Path, "svc.exe"));

        message.Should().NotBeNull();
        message.Should().Contain("writable by a non-administrator");
        message.Should().NotContain("does not resolve inside", "containment passed; the ACL is what refused");
    }

    [WindowsFact("Windows ACL APIs")]
    public void Refuses_a_target_reached_through_a_directory_junction()
    {
        // Junctions need no privilege, which is what makes them the realistic
        // redirection primitive: a link planted inside install_dir sends the
        // SYSTEM-level target anywhere the attacker likes while every textual
        // prefix check still says "contained".
        using var installDir = new TempDir();
        using var elsewhere = new TempDir();
        Junction.SweepStale(installDir.Path, JunctionPrefix);
        var link = Path.Combine(installDir.Path, JunctionPrefix + Guid.NewGuid().ToString("N"));

        Junction.CreateOrFail(link, elsewhere.Path);
        try
        {
            var message = PrivilegedTargetGuard.Check(
                "com_register", "path", installDir.Path, Path.Combine(link, "server.dll"));

            message.Should().NotBeNull();
            message.Should().Contain("junction");
        }
        finally
        {
            Junction.Remove(link);
        }
    }

    [WindowsFact("Windows ACL APIs")]
    public void Refuses_a_traversal_escape()
    {
        var anchor = MachineAnchor;

        PrivilegedTargetGuard.Check("firewall_rule", "program", anchor, Path.Combine(anchor, @"..\..\evil.exe"))
            .Should().NotBeNull();
    }

    [WindowsFact("Windows ACL APIs")]
    public void Refuses_a_sibling_directory_that_merely_shares_the_prefix()
    {
        var anchor = MachineAnchor;

        PrivilegedTargetGuard.Check("scheduled_task_create", "program", anchor, anchor + "Evil\\app.exe")
            .Should().NotBeNull();
    }

    [WindowsFact("Windows ACL APIs")]
    public void Refuses_when_no_install_dir_is_resolved()
    {
        var message = PrivilegedTargetGuard.Check(
            "scheduled_task_create", "program", installDir: null, @"C:\Program Files\App\app.exe");

        message.Should().NotBeNull();
        message.Should().Contain("no resolved install_dir");
    }
}
