namespace SigilBuild.Wrapper.Tests.Engine;

using System;
using System.IO;
using System.Runtime.Versioning;
using FluentAssertions;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Tests.Helpers;
using Xunit;

/// <summary>
/// R1: the machine-scope state directory is the trust boundary for elevated
/// replay. These tests assert it is refused when an unprivileged user could
/// have authored it.
/// </summary>
/// <remarks>
/// <c>[SupportedOSPlatform("windows")]</c> satisfies CA1416 for the
/// <c>StateDirectorySecurity</c> call sites (the same pattern
/// <c>Helpers/TestRegistry.cs</c> uses); <c>[WindowsFact]</c> is what makes the
/// tests report Skipped — rather than pass vacuously — on a non-Windows host.
/// </remarks>
[SupportedOSPlatform("windows")]
public class StateProvenanceTests
{
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
    public void IsAdminOnlyWritable_is_false_for_a_user_writable_container()
    {
        // Arrange — %TEMP% grants the interactive user FullControl, so anything
        // sited directly under it can be renamed or swapped by an unprivileged
        // process. Consumed by S2 (SYSTEM-level step targets) and S3 (staging).
        using var temp = new TempDir();
        var dir = Path.Combine(temp.Path, "staging");
        Directory.CreateDirectory(dir);

        // Act
        var adminOnly = StateDirectorySecurity.IsAdminOnlyWritable(dir);

        // Assert
        adminOnly.Should().BeFalse(
            "the container of a directory under the user's own %TEMP% is writable " +
            "by that user, so it is not a safe home for elevated-lifetime state");
    }

    [WindowsFact("Windows ACL APIs")]
    public void IsAdminOnlyWritable_fails_closed_when_the_container_is_missing()
    {
        // Arrange
        using var temp = new TempDir();
        var orphan = Path.Combine(temp.Path, "no-such-container", "child");

        // Act
        var adminOnly = StateDirectorySecurity.IsAdminOnlyWritable(orphan);

        // Assert
        adminOnly.Should().BeFalse("a container whose DACL cannot be read must fail closed");
    }

    [WindowsFact("Windows ACL APIs")]
    public void IsAdminOnlyWritable_is_true_for_an_admin_only_container()
    {
        // Arrange — %WINDIR%\System32 lives in %WINDIR%, whose DACL grants
        // BUILTIN\Users nothing beyond ReadAndExecute. A predicate that answered
        // false for everything would silently disable the S2/S3 gates built on it.
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        Directory.Exists(system32).Should().BeTrue(
            "the positive case needs a real admin-only container to assert against");

        // Act
        var adminOnly = StateDirectorySecurity.IsAdminOnlyWritable(system32);

        // Assert
        adminOnly.Should().BeTrue(
            "only SYSTEM, Administrators and TrustedInstaller hold write-class " +
            "rights on %WINDIR%");
    }
}
