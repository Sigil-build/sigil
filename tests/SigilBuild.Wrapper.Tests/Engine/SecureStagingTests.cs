namespace SigilBuild.Wrapper.Tests.Engine;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using FluentAssertions;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Tests.Helpers;
using Xunit;

/// <summary>
/// R12's primitive: <see cref="SecureStaging"/> — a private per-run staging
/// directory plus <c>OpenVerified</c>, which re-hashes a staged file from an open
/// handle whose sharing mode denies write and delete and hands that handle back to
/// be held across <c>Process.Start</c>.
/// </summary>
/// <remarks>
/// <para>
/// The decisive case is
/// <see cref="OpenVerified_throws_when_the_staged_file_changed_after_it_was_hashed"/>:
/// stage a file, take its hash, overwrite it, then ask for it back under the
/// original hash. That is exactly the swap R12 describes, and it must be a hard
/// refusal.
/// </para>
/// <para>
/// <b>This file runs UNELEVATED</b> (as the whole suite does). An unelevated process
/// cannot create a directory it is itself unable to modify, so
/// <c>IsAdminOnly</c>/<see cref="StateDirectorySecurity.IsAdminOnlyWritable"/> is
/// asserted <b>false</b> here and the admin-only root
/// (<c>%ProgramData%\Sigil\staging</c>) is never exercised locally — see
/// <see cref="Staging_is_admin_only_exactly_when_the_process_is_elevated"/>, which
/// asserts the branch it can actually reach and states the other. What does NOT
/// depend on elevation — the protected DACL, and every <c>OpenVerified</c>
/// guarantee — is asserted unconditionally.
/// </para>
/// <para>
/// <c>[SupportedOSPlatform("windows")]</c> satisfies CA1416 for the ACL call sites
/// (the pattern of <c>StateProvenanceTests</c>); <c>[WindowsFact]</c> is what makes
/// the Windows-only cases report Skipped rather than pass vacuously off Windows.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class SecureStagingTests
{
    /// <summary>
    /// Every staging line this file provokes. xUnit constructs the class per test, so
    /// this is per-test state.
    /// </summary>
    private readonly List<(string Message, bool IsError)> _reported = new();

    /// <summary>
    /// The only way this file stages. <c>SecureStaging.Create</c>'s report parameter is
    /// required so that no call site can quietly drop it; a test helper that passed a
    /// discarding sink would re-create exactly the omission the requirement exists to
    /// prevent, so everything lands in <see cref="_reported"/> and is asserted on —
    /// including the assertion that an unelevated fallback stays silent.
    /// </summary>
    private SecureStaging Staging(string purpose, string root) =>
        SecureStaging.Create(purpose, (message, isError) => _reported.Add((message, isError)), root);

    private static string Sha256Hex(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static string Stage(SecureStaging staging, string fileName, byte[] bytes)
    {
        var path = staging.PathFor(fileName);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    // ── The directory ─────────────────────────────────────────────────────────

    [Fact]
    public void Create_makes_a_fresh_private_directory_per_call()
    {
        using var root = new TempDir();

        using var first = Staging("prereq", root.Path);
        using var second = Staging("prereq", root.Path);

        Directory.Exists(first.Directory).Should().BeTrue();
        Directory.Exists(second.Directory).Should().BeTrue();
        second.Directory.Should().NotBe(first.Directory, "each run stages into its own freshly-named directory");
        Path.GetFileName(first.Directory).Should().StartWith("sigil-prereq-");
        Directory.GetFileSystemEntries(first.Directory).Should().BeEmpty("a fresh staging directory starts empty");
    }

    [Fact]
    public void Dispose_removes_the_staging_directory_and_its_contents()
    {
        using var root = new TempDir();
        string directory;

        using (var staging = Staging("update", root.Path))
        {
            directory = staging.Directory;
            Stage(staging, "setup.exe", Encoding.UTF8.GetBytes("payload"));
        }

        Directory.Exists(directory).Should().BeFalse("the staging directory is removed with the staged file in it");
    }

    [Fact]
    public void PathFor_refuses_a_name_that_would_escape_the_staging_directory()
    {
        using var root = new TempDir();
        using var staging = Staging("prereq", root.Path);

        var escape = () => staging.PathFor(Path.Combine("..", "elsewhere.exe"));
        var rooted = () => staging.PathFor(Path.Combine(root.Path, "elsewhere.exe"));

        escape.Should().Throw<ArgumentException>();
        rooted.Should().Throw<ArgumentException>();
        staging.PathFor("setup.exe").Should().Be(Path.Combine(staging.Directory, "setup.exe"));
    }

    [WindowsFact("Windows ACL APIs")]
    public void Created_directory_carries_a_protected_non_inherited_dacl()
    {
        using var root = new TempDir();
        using var staging = Staging("prereq", root.Path);

        var security = new DirectoryInfo(staging.Directory)
            .GetAccessControl(AccessControlSections.Access);

        security.AreAccessRulesProtected.Should().BeTrue(
            "the staging directory must discard whatever the parent (%TEMP% or a redirected root) grants");

        // Every writer must be the current user, SYSTEM or BUILTIN\Administrators —
        // nothing inherited, no CREATOR OWNER, no Users write.
        var allowedWriters = new[]
        {
            WindowsIdentity.GetCurrent().User!,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
        };

        foreach (FileSystemAccessRule rule in security.GetAccessRules(
            includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            rule.IsInherited.Should().BeFalse("a protected DACL leaves no inherited ACE behind");
            if (rule.AccessControlType != AccessControlType.Allow)
            {
                continue;
            }
            var writes = (rule.FileSystemRights &
                (FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete |
                 FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions |
                 FileSystemRights.TakeOwnership)) != 0;
            if (!writes)
            {
                continue;
            }
            allowedWriters.Should().Contain(
                (SecurityIdentifier)rule.IdentityReference,
                "only the staging process, SYSTEM and administrators may write into a staging directory");
        }
    }

    [WindowsFact("Windows ACL APIs")]
    public void Staging_is_admin_only_exactly_when_the_process_is_elevated()
    {
        using var root = new TempDir();
        using var staging = Staging("prereq", root.Path);

        // The single frozen predicate (S1) is the only answer consulted — SecureStaging
        // implements no second ACL check, so its own flag must agree with it.
        staging.IsAdminOnly.Should().Be(StateDirectorySecurity.IsAdminOnlyWritable(staging.Directory));

        // …and that agreement must not be two falses produced by a predicate that says
        // false to everything. %WINDIR%\System32 is TrustedInstaller-owned with only
        // privileged writers, so it is the control that keeps this assertion honest.
        StateDirectorySecurity.IsAdminOnlyWritable(
            Environment.GetFolderPath(Environment.SpecialFolder.System))
            .Should().BeTrue("the predicate must still answer true for a genuinely admin-only directory");

        if (Elevation.IsProcessElevated())
        {
            // ELEVATED: the admin-only root under %ProgramData%\Sigil\staging is
            // reachable, so the directory must be admin-only writable. NOT exercised on
            // the developer box that produced this file (unelevated); CI/an elevated run
            // is the arbiter.
            staging.IsAdminOnly.Should().BeTrue(
                "an elevated run stages under %ProgramData%\\Sigil\\staging, which is admin-only");
        }
        else
        {
            // UNELEVATED: an admin-only directory is unreachable by construction — this
            // process could not create something it cannot itself modify. The honest
            // answer is false, not a pretended true; OpenVerified is what carries the
            // guarantee in this branch.
            staging.IsAdminOnly.Should().BeFalse(
                "an unelevated process cannot create a directory only administrators can write");
            staging.Directory.Should().StartWith(root.Path, "unelevated staging falls back to the caller's root");
            _reported.Should().BeEmpty(
                "staging in the caller's root unelevated is the only option there is, not a downgrade — " +
                "reporting it would train an operator to ignore the line that does matter");
        }
    }

    // ── OpenVerified: case (b), the decisive one ──────────────────────────────

    [Fact]
    public void OpenVerified_throws_when_the_staged_file_changed_after_it_was_hashed()
    {
        using var root = new TempDir();
        using var staging = Staging("prereq", root.Path);

        // Stage a file and take the hash a downloader would have verified.
        var genuine = Encoding.UTF8.GetBytes("the-genuine-installer-bytes");
        var path = Stage(staging, "setup.exe", genuine);
        var verifiedHash = Sha256Hex(genuine);

        // The attacker's window: between the download's verify and the launch, the
        // bytes are replaced. On the pre-fix code path nothing was holding the file
        // and nothing looked at it again — it was simply executed.
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("attacker-substituted-payload"));

        var open = () => staging.OpenVerified("setup.exe", verifiedHash);

        open.Should().Throw<StagedFileVerificationException>(
                "a staged file whose bytes changed after verification must never be handed back for launch")
            .WithMessage("*replaced after verification*");
    }

    [Fact]
    public void OpenVerified_returns_a_readable_handle_positioned_at_zero_when_the_hash_still_matches()
    {
        using var root = new TempDir();
        using var staging = Staging("prereq", root.Path);
        var bytes = Encoding.UTF8.GetBytes("the-genuine-installer-bytes");
        Stage(staging, "setup.exe", bytes);

        using var handle = staging.OpenVerified("setup.exe", Sha256Hex(bytes));

        handle.Position.Should().Be(0, "the handle is rewound after the re-hash so the caller can read it");
        using var reader = new BinaryReader(handle, Encoding.UTF8, leaveOpen: true);
        reader.ReadBytes(bytes.Length).Should().Equal(bytes);
    }

    [Fact]
    public void OpenVerified_is_case_insensitive_about_the_expected_digest()
    {
        using var root = new TempDir();
        using var staging = Staging("prereq", root.Path);
        var bytes = Encoding.UTF8.GetBytes("case-insensitive-digest");
        Stage(staging, "setup.exe", bytes);

        using var handle = staging.OpenVerified("setup.exe", Sha256Hex(bytes).ToUpperInvariant());

        // An upper-case digest must be accepted on the same terms as a lower-case one —
        // same rewound handle over the same verified bytes, not merely "did not throw".
        handle.Position.Should().Be(0);
        handle.Length.Should().Be(bytes.Length);
        using var reader = new BinaryReader(handle, Encoding.UTF8, leaveOpen: true);
        reader.ReadBytes(bytes.Length).Should().Equal(bytes);
    }

    [Fact]
    public void OpenVerified_refuses_a_file_with_no_expected_digest_at_all()
    {
        using var root = new TempDir();
        using var staging = Staging("prereq", root.Path);
        Stage(staging, "setup.exe", Encoding.UTF8.GetBytes("x"));

        var open = () => staging.OpenVerified("setup.exe", "   ");

        open.Should().Throw<StagedFileVerificationException>(
            "an unverifiable staged binary is refused, never launched on trust");
    }

    // ── OpenVerified: case (c), the handle the caller holds ───────────────────

    [WindowsFact("Windows file sharing semantics")]
    public void The_returned_handle_denies_write_and_delete_but_still_admits_readers()
    {
        using var root = new TempDir();
        using var staging = Staging("prereq", root.Path);
        var bytes = Encoding.UTF8.GetBytes("held-across-the-launch");
        var path = Stage(staging, "setup.exe", bytes);

        using var handle = staging.OpenVerified("setup.exe", Sha256Hex(bytes));

        // FileShare.Read denies FILE_WRITE_DATA and DELETE to everyone else …
        var overwrite = () => File.WriteAllBytes(path, Encoding.UTF8.GetBytes("swapped"));
        var delete = () => File.Delete(path);
        overwrite.Should().Throw<IOException>("the held handle must deny write for as long as it lives");
        delete.Should().Throw<IOException>("the held handle must deny delete too — a swap can be a delete + recreate");

        // … while still admitting readers, which FileShare.None would have broken.
        File.ReadAllBytes(path).Should().Equal(bytes);
    }

    [WindowsFact("spawns a real child process")]
    public void The_held_handle_still_permits_the_staged_file_to_be_launched()
    {
        // The reason the sharing mode is FileShare.Read and not FileShare.None:
        // CreateProcess opens the image for read+execute, which None would refuse with
        // a sharing violation — the protection would break the launch it protects.
        using var root = new TempDir();
        using var staging = Staging("prereq", root.Path);

        var source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        var staged = staging.PathFor("staged.exe");
        File.Copy(source, staged);
        var hash = Sha256Hex(File.ReadAllBytes(staged));

        using var handle = staging.OpenVerified("staged.exe", hash);

        var psi = new ProcessStartInfo
        {
            FileName = staged,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("exit /b 7");

        using var process = Process.Start(psi);
        process.Should().NotBeNull("holding the verified handle must not block the launch");
        process!.WaitForExit();
        process.ExitCode.Should().Be(7, "the launched child is the staged file whose bytes were verified");
    }

    // ── The elevated run refuses, loudly, and never repairs S1's state root ──

    /// <summary>
    /// <c>TryResolveAdminOnlyRoot</c> takes the state-root base as a parameter precisely
    /// so these can run unelevated against a throwaway directory instead of the real
    /// <c>%ProgramData%</c>. Unelevated, a directory this process creates is owned by
    /// this process, so the admin-only check fails — which is the refusal case, and is
    /// exactly what has to be observable.
    /// </summary>
    [WindowsFact("Windows ACL APIs")]
    public void An_elevated_run_that_cannot_get_an_admin_only_root_reports_why()
    {
        using var root = new TempDir();
        var reported = new List<(string Message, bool IsError)>();

        var resolved = SecureStaging.TryResolveAdminOnlyRoot(
            root.Path, (m, e) => reported.Add((m, e)));

        resolved.Should().BeNull("this process is not elevated, so it cannot own an admin-only root");
        reported.Should().ContainSingle(
            "a refusal that says nothing is indistinguishable from an unrelated failure");
        reported[0].IsError.Should().BeTrue("losing containment is not an informational line");
        reported[0].Message.Should().Contain("REFUSED");
        reported[0].Message.Should().Contain(
            "substitute what this process launches", "the report must say what the risk actually is");
        reported[0].Message.Should().Contain(
            Path.Combine(root.Path, "Sigil"), "the report must name the directory that failed the check");
    }

    /// <summary>
    /// The routed R5 residual, and the policy decision made for it: an <b>elevated</b>
    /// run that cannot obtain an administrator-only staging root <b>refuses</b> rather
    /// than staging in a directory the current user can also write.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ResolveRoot</c> takes <c>elevated</c> and <c>commonAppData</c> as parameters so
    /// the elevated branch is reachable from this unelevated test process — the only way
    /// to assert the refusal on a developer box or an unelevated runner, since the real
    /// branch depends on a token this process does not have.
    /// </para>
    /// <para>
    /// Pre-fix, this path reported a degrade and handed back <c>%TEMP%</c>; the negative
    /// assertion is that it no longer returns anything at all.
    /// </para>
    /// </remarks>
    [WindowsFact("Windows ACL APIs")]
    public void An_elevated_run_refuses_to_stage_when_it_cannot_get_an_admin_only_root()
    {
        using var root = new TempDir();
        var reported = new List<(string Message, bool IsError)>();

        var resolve = () => SecureStaging.ResolveRoot(
            fallbackRoot: root.Path,
            report: (m, e) => reported.Add((m, e)),
            elevated: true,
            commonAppData: root.Path);

        resolve.Should()
            .Throw<StagingSecurityException>(
                "an elevated process that stages a downloaded executable where an unprivileged process can " +
                "also write it is handing that process an elevated launch — there is no weakened-but-useful " +
                "version of that, so it refuses")
            .WithMessage("*not administrator-only writable*",
                "the thrown message must carry the cause, because a call site with no progress sink attached " +
                "would otherwise lose it entirely");

        reported.Should().ContainSingle("the reason is reported as well as thrown");
        reported[0].IsError.Should().BeTrue();
    }

    /// <summary>
    /// The other side of the same policy: staging in <c>%TEMP%</c> <b>un</b>elevated is
    /// the only option there is, not a downgrade. It must neither throw nor report —
    /// crying wolf here would train an operator to ignore the line that does matter.
    /// </summary>
    [Fact]
    public void An_unelevated_run_stages_in_the_fallback_root_silently()
    {
        using var root = new TempDir();
        var reported = new List<(string Message, bool IsError)>();

        var (resolved, isAdminOnly) = SecureStaging.ResolveRoot(
            fallbackRoot: root.Path,
            report: (m, e) => reported.Add((m, e)),
            elevated: false,
            commonAppData: root.Path);

        resolved.Should().Be(root.Path);
        isAdminOnly.Should().BeFalse(
            "an unelevated process cannot create a directory only administrators can write");
        reported.Should().BeEmpty();
    }

    [WindowsFact("Windows ACL APIs")]
    public void An_existing_untrusted_state_root_is_reported_and_left_unrepaired()
    {
        // %ProgramData%\Sigil is lane S1's install-state root. Staging a download must
        // never re-permission it or take ownership of it as a side effect — that repair
        // belongs to the install path, where the decision is made deliberately.
        using var root = new TempDir();
        var sigil = Path.Combine(root.Path, "Sigil");
        Directory.CreateDirectory(sigil); // plain, inheriting, this-user-owned
        var before = new DirectoryInfo(sigil).GetAccessControl(AccessControlSections.Access);
        before.AreAccessRulesProtected.Should().BeFalse("precondition: the pre-existing root inherits its ACL");

        var reported = new List<(string Message, bool IsError)>();
        var resolved = SecureStaging.TryResolveAdminOnlyRoot(root.Path, (m, e) => reported.Add((m, e)));

        resolved.Should().BeNull();
        reported.Should().ContainSingle();
        reported[0].Message.Should().Contain("not repaired");
        reported[0].Message.Should().Contain("install state store");

        var after = new DirectoryInfo(sigil).GetAccessControl(AccessControlSections.Access);
        after.AreAccessRulesProtected.Should().BeFalse(
            "an existing state root must be left exactly as found — hardening it here would be S1's repair " +
            "happening as a side effect of staging a download");
        Directory.Exists(Path.Combine(sigil, "staging")).Should().BeFalse(
            "nothing is created underneath a state root that failed the check");
    }

    [WindowsFact("Windows ACL APIs")]
    public void The_degrade_report_names_the_exception_type_and_message_when_the_root_cannot_be_created()
    {
        // A state-root base that is a FILE, not a directory — the creation throws, and
        // the cause must survive into the report rather than being swallowed.
        using var root = new TempDir();
        var notADirectory = Path.Combine(root.Path, "not-a-directory");
        File.WriteAllText(notADirectory, "x");

        var reported = new List<(string Message, bool IsError)>();
        var resolved = SecureStaging.TryResolveAdminOnlyRoot(notADirectory, (m, e) => reported.Add((m, e)));

        resolved.Should().BeNull();
        reported.Should().ContainSingle();
        reported[0].IsError.Should().BeTrue();
        reported[0].Message.Should().Contain("could not be established");
        reported[0].Message.Should().MatchRegex(
            @"[A-Za-z]+Exception: .+",
            "the swallowed cause is what tells an operator whether this was a redirected root, a denied " +
            "ACL write, or something provoked deliberately — its type AND message must be recorded");
    }

    [Fact]
    public void OpenVerified_after_dispose_is_refused()
    {
        using var root = new TempDir();
        var staging = Staging("prereq", root.Path);
        staging.Dispose();

        var open = () => staging.OpenVerified("setup.exe", new string('a', 64));

        open.Should().Throw<ObjectDisposedException>();
    }
}
