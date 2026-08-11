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
/// asserted <b>false</b> here and the elevated siting
/// (<c>%ProgramData%\sigil-{purpose}-{guid}</c>) is never exercised positively
/// locally — see
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

    /// <summary>
    /// <c>IsAdminOnly</c> is never anything but the frozen predicate's own answer about
    /// the directory that was created — this type implements no second ACL check.
    /// </summary>
    /// <remarks>
    /// <b>This asserts the unelevated contract unconditionally, on every host.</b> It goes
    /// through the <em>public</em> entry point, and the assembly-wide test floor forces the
    /// unelevated siting there, so branching on <c>Elevation.IsProcessElevated()</c> would
    /// now be wrong: on an elevated runner the token says "elevated" while the siting is
    /// deliberately not. An earlier revision did branch that way and passed on CI only
    /// because the suite was still staging into the real <c>%ProgramData%</c> — the bug.
    /// The elevated contract moved to
    /// <see cref="The_elevated_siting_agrees_with_the_frozen_predicate_or_refuses"/>, which
    /// asks for that siting explicitly.
    /// </remarks>
    [WindowsFact("Windows ACL APIs")]
    public void Staging_through_the_public_entry_point_agrees_with_the_frozen_predicate()
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

        staging.IsAdminOnly.Should().BeFalse(
            "the test floor pins the unelevated siting, and a process staging in a root it owns cannot " +
            "produce a directory only administrators can write");
        staging.Directory.Should().StartWith(
            root.Path, "the unelevated siting stages in the caller's own root");
        _reported.Should().BeEmpty(
            "staging in the caller's root unelevated is the only option there is, not a downgrade — " +
            "reporting it would train an operator to ignore the line that does matter");
    }

    /// <summary>
    /// The elevated contract, asked for explicitly through the internal overload so it does
    /// not depend on the host's token matching a siting the test floor has pinned.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Elevated host:</b> the hardened directory really is administrator-only, the frozen
    /// predicate agrees, and it is a direct child of the root it was given. That is the
    /// premise the whole lane rests on — if <c>CreateHardened</c>'s output did not satisfy
    /// <c>IsAdminOnlyWritable</c>, every elevated install would refuse — so this test is how
    /// CI states it rather than assuming it.
    /// </para>
    /// <para>
    /// <b>Unelevated host:</b> the confirmation cannot pass, so the refusal is the contract;
    /// nothing is left behind. The <c>catch</c> re-asserts that the process really is
    /// unelevated, so an <em>elevated</em> runner that wrongly refuses fails here instead of
    /// being quietly absorbed.
    /// </para>
    /// <para>Neither branch is vacuous, and exactly one runs on any given host.</para>
    /// </remarks>
    [WindowsFact("Windows ACL APIs")]
    public void The_elevated_siting_agrees_with_the_frozen_predicate_or_refuses()
    {
        using var root = new TempDir();
        var reported = new List<(string Message, bool IsError)>();

        try
        {
            using var staging = SecureStaging.Create(
                "prereq", (m, e) => reported.Add((m, e)), fallbackRoot: null,
                elevated: true, commonAppData: root.Path);

            // ELEVATED HOST.
            staging.IsAdminOnly.Should().BeTrue(
                "creation and confirmation both succeeded, so the directory is administrator-only by " +
                "construction");
            StateDirectorySecurity.IsAdminOnlyWritable(staging.Directory).Should().BeTrue(
                "IsAdminOnly is the frozen predicate's answer, not a second opinion");
            Path.GetDirectoryName(staging.Directory).Should().Be(
                root.Path, "the per-run directory is a DIRECT child of the machine-wide root");
            Path.GetFileName(staging.Directory).Should().MatchRegex("^sigil-prereq-[0-9a-f]{32}$");
            reported.Should().BeEmpty("a successful elevated staging has nothing to report");
        }
        catch (StagingSecurityException ex)
        {
            // UNELEVATED HOST.
            Elevation.IsProcessElevated().Should().BeFalse(
                "only a process that cannot own an administrator-only directory may fail this " +
                "confirmation — an ELEVATED run reaching here would mean CreateHardened's output does " +
                "not satisfy IsAdminOnlyWritable, which would make every real elevated install refuse");
            ex.Message.Should().Contain("administrator-only writable");
            reported.Should().Contain(
                r => r.IsError && r.Message.Contains("REFUSED", StringComparison.Ordinal));
            Directory.GetDirectories(root.Path).Should().BeEmpty(
                "a directory that failed the confirmation is removed again — nothing was staged in it");
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

    // ── Where an elevated run stages, and what it refuses ─────────────────────

    /// <summary>
    /// The routed R5 residual and its policy: an <b>elevated</b> run that cannot obtain
    /// an administrator-only staging directory <b>refuses</b> rather than staging where
    /// the current user can also write.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The internal <c>Create</c> overload takes <c>elevated</c> and <c>commonAppData</c>
    /// so the elevated branch is reachable from this unelevated test process — the only
    /// way to assert the refusal on a developer box or an unelevated runner, since the
    /// real branch depends on a token this process does not have.
    /// </para>
    /// <para>
    /// <b>The refusal is provoked by something genuinely un-creatable on any host.</b> An
    /// earlier version pointed at an ordinary empty scratch directory and relied on the
    /// created child failing the administrator-only confirmation — true here, but false on
    /// an <em>elevated</em> runner, where the child really would be administrator-owned and
    /// staging must correctly proceed. That test could only pass in the world where
    /// production is broken. This one denies
    /// <see cref="FileSystemRights.CreateDirectories"/> to <c>Everyone</c> on the parent,
    /// which stops the create for every caller at every privilege level.
    /// </para>
    /// <para>
    /// Pre-fix this path reported a degrade and handed back <c>%TEMP%</c>; the negative
    /// assertion is that it now hands back nothing at all, and leaves nothing behind.
    /// </para>
    /// </remarks>
    [WindowsFact("Windows ACL APIs")]
    public void An_elevated_run_refuses_to_stage_when_the_directory_cannot_be_created()
    {
        using var root = new TempDir();
        var locked = Path.Combine(root.Path, "locked");
        DenyCreateDirectories(locked);

        var reported = new List<(string Message, bool IsError)>();
        var create = () => SecureStaging.Create(
            "prereq",
            (m, e) => reported.Add((m, e)),
            fallbackRoot: null,
            elevated: true,
            commonAppData: locked);

        create.Should()
            .Throw<StagingSecurityException>(
                "an elevated process that stages a downloaded executable where an unprivileged process can " +
                "also write it is handing that process an elevated launch — there is no weakened-but-useful " +
                "version of that, so every way of failing to get an administrator-only directory refuses")
            .WithMessage("*could not be created*",
                "the thrown message must carry the cause, because a call site with no progress sink attached " +
                "would otherwise lose it entirely");

        reported.Should().Contain(
            r => r.IsError && r.Message.Contains("REFUSED", StringComparison.Ordinal),
            "a refusal that says nothing is indistinguishable from an unrelated failure");
        Directory.GetDirectories(locked).Should().BeEmpty("nothing was staged");
    }

    /// <summary>
    /// The guard behind this lane's "no test writes to a real <c>%ProgramData%</c>"
    /// claim, asserted through the <b>production</b> entry point — the same
    /// <c>SecureStaging.Create(purpose, report, fallbackRoot)</c> that
    /// <c>PrerequisiteRunner</c>, <c>UpdateRunner</c> and every <c>{staging_dir}</c>
    /// resolution call.
    /// </summary>
    /// <remarks>
    /// Unelevated this passes trivially. On an <b>elevated</b> runner — which CI is — it
    /// fails the moment the assembly-wide floor in <c>TestAssemblySetup</c> is removed,
    /// because staging would then correctly choose <c>%ProgramData%</c>. That is what
    /// makes this a real guard rather than a restatement.
    /// </remarks>
    [Fact]
    public void The_production_entry_point_never_stages_into_the_real_program_data_under_test()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        using var root = new TempDir();

        using var staging = SecureStaging.Create("prereq", (_, _) => { }, root.Path);

        staging.Directory.Should().NotStartWith(
            programData + Path.DirectorySeparatorChar,
            "CI runs elevated, so without the assembly-wide floor every staging call in the suite would " +
            "create a directory in the real machine-wide data root and launch binaries out of it");
        staging.Directory.Should().StartWith(
            root.Path, "the caller's own fallback root is still honoured — the floor only disables the " +
            "elevated branch, it does not hijack where a test asked to stage");
        staging.IsAdminOnly.Should().BeFalse("the elevated siting is exactly what the floor turns off");
    }

    /// <summary>
    /// Staging must never delete anything it did not create. The specific composition that
    /// made this a live defect: the native-runtime DLL cache lives beside staging under
    /// <c>%ProgramData%</c> and its name starts with the same <c>sigil-</c> prefix, so a
    /// revision that swept <c>sigil-*</c> siblings older than a day matched
    /// <c>%ProgramData%\sigil-runtime</c> exactly — and could recursively delete the
    /// directory the same elevated process was loading Skia and ANGLE from, mid-install.
    /// </summary>
    /// <remarks>
    /// <b>This assertion is only sharp on an elevated runner.</b> The sweep it guards
    /// against skipped candidates that failed <c>IsAdminOnlyWritable</c>, and an unelevated
    /// process cannot create a directory that passes it — so unelevated this passes either
    /// way, and it is CI, running elevated, that would actually have caught the deletion.
    /// The fix itself is a removal: there is no sweep any more.
    /// </remarks>
    [WindowsFact("Windows ACL APIs")]
    public void Staging_never_deletes_a_sibling_it_did_not_create()
    {
        using var root = new TempDir();

        // The native-runtime cache, as it sits beside staging: same prefix, long-lived,
        // holding a DLL the process may currently have mapped.
        var runtimeCache = Path.Combine(root.Path, "sigil-runtime");
        Directory.CreateDirectory(runtimeCache);
        var loadedDll = Path.Combine(runtimeCache, "libSkiaSharp.dll");
        File.WriteAllBytes(loadedDll, new byte[] { 1, 2, 3 });
        Directory.SetCreationTimeUtc(runtimeCache, DateTime.UtcNow.AddDays(-30));

        try
        {
            using var staging = SecureStaging.Create(
                "stage", (_, _) => { }, fallbackRoot: null, elevated: true, commonAppData: root.Path);
        }
        catch (StagingSecurityException)
        {
            // Unelevated the confirmation cannot pass, so this is the expected outcome
            // here. What matters either way is what survived the attempt.
        }

        Directory.Exists(runtimeCache).Should().BeTrue(
            "resolving a staging directory must not remove a sibling — least of all the native-runtime " +
            "cache the wizard loads Skia from");
        File.ReadAllBytes(loadedDll).Should().Equal(
            new byte[] { 1, 2, 3 }, "and certainly not the DLLs inside it");
    }

    /// <summary>
    /// Create <paramref name="path"/> and deny <c>Everyone</c> the right to create
    /// directories inside it, so a child cannot be created at any privilege level — Deny
    /// beats Allow in the Windows access check, administrators included. Only that one
    /// right is denied, so the directory itself stays deletable and <see cref="TempDir"/>
    /// can still clean up.
    /// </summary>
    private static void DenyCreateDirectories(string path)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        security.AddAccessRule(new FileSystemAccessRule(
            everyone, FileSystemRights.FullControl, InheritanceFlags.None,
            PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            everyone, FileSystemRights.CreateDirectories, InheritanceFlags.None,
            PropagationFlags.None, AccessControlType.Deny));
        security.CreateDirectory(path);

        var probe = () => Directory.CreateDirectory(Path.Combine(path, "probe"));
        probe.Should().Throw<UnauthorizedAccessException>(
            "precondition: the parent must genuinely refuse child creation, or this test proves nothing");
    }

    /// <summary>
    /// The denial of service that an earlier revision of this type created, and that this
    /// siting removes. Staging used to live at <c>%ProgramData%\Sigil\staging</c> and
    /// refuse when the intermediate <c>Sigil</c> directory was not administrator-only —
    /// but that directory is the install-state store's (so this type must not repair it)
    /// and <b>any unprivileged user can create it</b>, which made the refusal above a
    /// lever anyone could pull against every elevated install.
    /// </summary>
    [WindowsFact("Windows ACL APIs")]
    public void A_squatted_state_root_neither_blocks_an_elevated_run_nor_is_touched_by_it()
    {
        using var root = new TempDir();

        // The squat: an ordinary, this-user-owned, inheriting directory — exactly what a
        // non-administrator can create under the real %ProgramData% with no privilege at
        // all. Under the previous siting this alone made every elevated run refuse.
        var sigil = Path.Combine(root.Path, "Sigil");
        Directory.CreateDirectory(sigil);
        new DirectoryInfo(sigil).GetAccessControl(AccessControlSections.Access)
            .AreAccessRulesProtected.Should().BeFalse("precondition: the squatted directory inherits its ACL");

        var reported = new List<(string Message, bool IsError)>();
        var (resolved, adminOnly) = SecureStaging.ResolveRoot(
            fallbackRoot: null,
            report: (m, e) => reported.Add((m, e)),
            elevated: true,
            commonAppData: root.Path);

        resolved.Should().Be(
            root.Path,
            "the per-run directory is created as a DIRECT child of %ProgramData% — it descends through no " +
            "fixed name an unprivileged user could have created first");
        adminOnly.Should().BeTrue();
        reported.Should().BeEmpty(
            "a squatted state root is no longer a reason to refuse, so there is nothing to report");

        Directory.GetFileSystemEntries(sigil).Should().BeEmpty(
            "the install-state store's root is neither repaired nor written to from the staging path");
        new DirectoryInfo(sigil).GetAccessControl(AccessControlSections.Access)
            .AreAccessRulesProtected.Should().BeFalse(
                "it must be left exactly as found — re-permissioning it here would be the state store's " +
                "repair happening as a side effect of staging a download");
        Directory.GetDirectories(root.Path).Should().BeEquivalentTo(
            new[] { sigil },
            "resolving the elevated root creates nothing of its own — in particular nothing named Sigil");
    }

    /// <summary>
    /// The other half of the same property: the per-run directory's name carries a GUID,
    /// so there is no path an attacker can pre-create and have adopted. Every plausible
    /// fixed name is planted here and none of them is used.
    /// </summary>
    [Fact]
    public void The_staging_directory_is_unpredictable_and_no_pre_created_one_is_adopted()
    {
        using var root = new TempDir();

        var predictable = new[]
        {
            Path.Combine(root.Path, "Sigil"),
            Path.Combine(root.Path, "Sigil", "staging"),
            Path.Combine(root.Path, "staging"),
            Path.Combine(root.Path, "sigil-prereq"),
            Path.Combine(root.Path, "sigil-prereq-0"),
        };
        foreach (var path in predictable)
        {
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "planted.exe"), "attacker payload");
        }

        using var staging = Staging("prereq", root.Path);

        predictable.Should().NotContain(
            staging.Directory, "a directory an attacker could have created first must never be adopted");
        Path.GetFileName(staging.Directory).Should().MatchRegex(
            "^sigil-prereq-[0-9a-f]{32}$",
            "the per-run GUID is the whole reason a pre-planted path cannot be hit — %ProgramData% lets any " +
            "user CREATE a child, so only an unguessable name is safe there");
        Directory.GetFileSystemEntries(staging.Directory).Should().BeEmpty(
            "the staging directory is freshly created, never an existing one that happened to be there");

        foreach (var path in predictable)
        {
            File.Exists(Path.Combine(path, "planted.exe")).Should().BeTrue(
                "staging neither adopts nor disturbs directories it did not create");
        }
    }

    /// <summary>
    /// Staging in <c>%TEMP%</c> <b>un</b>elevated is the only option there is, not a
    /// downgrade. It must neither throw nor report — crying wolf here would train an
    /// operator to ignore the line that does matter.
    /// </summary>
    [Fact]
    public void An_unelevated_run_stages_in_the_fallback_root_silently()
    {
        using var root = new TempDir();
        var reported = new List<(string Message, bool IsError)>();

        using var staging = SecureStaging.Create(
            "prereq", (m, e) => reported.Add((m, e)), root.Path, elevated: false, commonAppData: root.Path);

        Path.GetDirectoryName(staging.Directory).Should().Be(root.Path);
        staging.IsAdminOnly.Should().BeFalse(
            "an unelevated process cannot create a directory only administrators can write");
        reported.Should().BeEmpty();
    }

    /// <summary>
    /// <c>%ProgramData%</c> is an OS directory and is never created here. If it is
    /// missing, or is a file, the environment is broken enough that guessing is worse
    /// than refusing — and the cause must reach both the report and the exception.
    /// </summary>
    [WindowsFact("Windows ACL APIs")]
    public void An_elevated_run_refuses_when_the_machine_wide_root_is_not_a_directory()
    {
        using var root = new TempDir();
        var notADirectory = Path.Combine(root.Path, "not-a-directory");
        File.WriteAllText(notADirectory, "x");

        var reported = new List<(string Message, bool IsError)>();
        var create = () => SecureStaging.Create(
            "prereq",
            (m, e) => reported.Add((m, e)),
            fallbackRoot: null,
            elevated: true,
            commonAppData: notADirectory);

        create.Should().Throw<StagingSecurityException>()
            .WithMessage("*does not exist as a directory*");

        reported.Should().ContainSingle();
        reported[0].IsError.Should().BeTrue();
        reported[0].Message.Should().Contain("REFUSED");
        reported[0].Message.Should().Contain(
            "substitute what this process launches", "the report must say what the risk actually is");
        reported[0].Message.Should().Contain(
            notADirectory, "the report must name the location that failed the check");
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
