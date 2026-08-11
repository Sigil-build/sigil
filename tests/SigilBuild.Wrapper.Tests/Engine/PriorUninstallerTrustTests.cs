namespace SigilBuild.Wrapper.Tests.Engine;

using System;
using System.IO;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Win32;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Cli;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Tests.Helpers;
using Xunit;

/// <summary>
/// R2: the upgrade path took an executable path out of the Add/Remove-Programs
/// registry and spawned it from an already-elevated process. Two independent
/// defects made that reachable by any standard user, and both are asserted here.
/// </summary>
/// <remarks>
/// <para>
/// <b>Defect 1 — the hive.</b> A machine-scope resolve fell back to HKCU when HKLM
/// held no entry. HKCU is writable without any privilege, so the attacker chose the
/// <c>UninstallString</c>.
/// </para>
/// <para>
/// <b>Defect 2 — the spawn.</b> The only gate before <c>Process.Start</c> was
/// <c>File.Exists</c>: no signature check, no path validation.
/// </para>
/// <para>
/// <c>[SupportedOSPlatform("windows")]</c> satisfies CA1416 for the
/// <see cref="TestRegistry"/> call sites; <c>[WindowsFact]</c> is what makes these
/// report Skipped — rather than pass vacuously — on a non-Windows host (register
/// row R6).
/// </para>
/// <para>
/// <b>Nothing here writes HKLM.</b> The planted ARP entry is HKCU-only and
/// uniquely named, and is removed in a <c>finally</c> via <c>using</c>.
/// </para>
/// <para>
/// <b>No process is ever created</b> — which is a weaker claim than "no test calls
/// <c>Process.Start</c>", and the difference is the point.
/// <see cref="An_unelevated_per_user_upgrade_of_an_unsigned_uninstaller_is_not_gated"/>
/// deliberately DOES reach <c>Process.Start</c> on an unelevated host: reaching it is
/// the assertion, because that is what proves the gate did not fire. What it starts is
/// <c>StubExe</c>'s two-byte <c>MZ</c> file, which is not a valid Win32 image, so the
/// loader rejects it and <c>Process.Start</c> throws before any process exists — which
/// is why the engine reports "failed to run". Every other test asserts through the
/// engine's typed outcome or the predicate directly and never reaches a spawn at all.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class PriorUninstallerTrustTests
{
    // ---- Defect 1: the hive -------------------------------------------------

    [WindowsFact("Windows registry")]
    public void Machine_scope_resolve_ignores_an_HKCU_arp_entry()
    {
        // Arrange — exactly the attacker's move: an ARP entry in the user's own
        // hive, with a DisplayVersion low enough to classify as an upgrade and an
        // UninstallString aimed at a binary the user controls.
        var appId = "sigil.test." + Guid.NewGuid().ToString("N");
        using var planted = TestRegistry.PlantUserUninstallEntry(
            appId,
            displayVersion: "0.0.1",
            uninstallString: @"""C:\Users\Public\evil.exe"" /S /Uninstall");

        // Act
        var machine = InstalledStateResolver.Resolve(appId, InstallScope.Machine);

        // Assert
        machine.Found.Should().BeFalse(
            "a machine-scope resolve must not read the user hive — HKCU is writable " +
            "by the unprivileged user whose exe would then be spawned by the elevated " +
            "installer");
        machine.Should().Be(UpgradeState.None);
    }

    /// <summary>
    /// The non-vacuity control for the test above: deleting the registry read
    /// entirely — or a fixture that silently failed to plant the key — must not leave
    /// it green. The fixture is verified through the raw registry API, which does not
    /// depend on the process token, and only then is the resolver's answer asserted
    /// against the contract for the token this run actually has.
    /// </summary>
    [WindowsFact("Windows registry")]
    public void The_same_HKCU_entry_is_readable_and_is_resolved_per_the_elevation_contract()
    {
        var appId = "sigil.test." + Guid.NewGuid().ToString("N");
        using var planted = TestRegistry.PlantUserUninstallEntry(
            appId,
            displayVersion: "0.0.1",
            uninstallString: @"""C:\Users\Public\evil.exe"" /S /Uninstall");

        // The fixture really did plant a readable ARP entry — asserted independently
        // of the resolver and of the process token.
        using (var raw = Registry.CurrentUser.OpenSubKey(planted.Path))
        {
            raw.Should().NotBeNull();
            raw!.GetValue("DisplayVersion").Should().Be("0.0.1");
        }

        var user = InstalledStateResolver.Resolve(appId, InstallScope.User);

        if (Elevation.IsProcessElevated())
        {
            // R2's second half: an ELEVATED user-scope run is a privilege boundary
            // too, so it must not read HKCU either.
            user.Found.Should().BeFalse(
                "an elevated process must not resolve an ARP entry out of the user hive, " +
                "whatever scope it was asked for");
        }
        else
        {
            user.Found.Should().BeTrue(
                "an unelevated per-user run legitimately reads its own hive — refusing " +
                "that would break every per-user upgrade");
            user.InstalledVersion.Should().Be("0.0.1");
            user.PriorUninstallExe.Should().Be(@"C:\Users\Public\evil.exe");
            user.FoundScope.Should().Be(InstallScope.User);
        }
    }

    /// <summary>
    /// The probe order itself, in both elevation branches, against the pure seam.
    /// </summary>
    [WindowsTheory("Windows registry")]
    [InlineData(InstallScope.Machine, false)]
    [InlineData(InstallScope.Machine, true)]
    [InlineData(InstallScope.User, true)]
    public void Hkcu_is_never_probed_when_privilege_is_at_stake(InstallScope scope, bool elevated)
    {
        InstalledStateResolver.ScopeProbeOrder(scope, elevated)
            .Should().Equal(InstallScope.Machine);
    }

    /// <summary>
    /// The user → HKLM fallback, which is deliberately kept and had no coverage at
    /// all. Asserted against the pure seam rather than by planting an HKLM key: that
    /// would need elevation and would mutate the real machine's installed-programs
    /// list. Passing <c>elevated</c> as a parameter is what lets the branch this
    /// unelevated session cannot otherwise reach be tested at the same time.
    /// </summary>
    [WindowsFact("Windows registry")]
    public void An_unelevated_user_scope_probe_reads_HKCU_first_and_then_falls_back_to_HKLM()
    {
        InstalledStateResolver.ScopeProbeOrder(InstallScope.User, elevated: false)
            .Should().Equal(
                new[] { InstallScope.User, InstallScope.Machine },
                "a per-user install must still discover a prior MACHINE install so the " +
                "existing scope can win — reading a hive the caller cannot write is safe, " +
                "and dropping this fallback would silently break cross-scope upgrades");
    }

    // ---- Defect 2: the spawn ------------------------------------------------

    /// <summary>
    /// The end-to-end assertion, driven through the real
    /// <see cref="InstallSession.RunInstallCoreAsync"/> upgrade teardown with an
    /// injected installed state. The uninstaller is a two-byte "MZ" stub in the
    /// user's temp directory — user-writable and unsigned — so the run must abort
    /// with the refusal message, never with a spawn error.
    /// </summary>
    /// <remarks>
    /// The distinction matters and is what makes this a true negative test: before
    /// the fix the engine reached <c>Process.Start</c> and returned "failed to run"
    /// (the stub is not a valid Win32 image), which is the same abort for entirely
    /// the wrong reason. Asserting the wording pins the refusal, not the accident.
    /// </remarks>
    [WindowsFact("Windows-only elevation path")]
    public async Task Prior_uninstaller_in_a_user_writable_path_is_not_spawned()
    {
        // Arrange
        using var temp = new TempDir();
        var fakeUninstaller = StubExe(temp, "evil.exe");

        // Machine scope: the run either is elevated or is about to relaunch itself
        // elevated, so the gate applies whatever token this test process holds.
        var outcome = await RunUpgradeAsync(InstallScope.Machine, temp.Path, fakeUninstaller);

        // Assert
        outcome.Error.Should().Contain(
            "is not verified",
            "the elevated process must refuse a registry-supplied uninstaller that is " +
            "neither signed nor admin-writable-only — reaching Process.Start at all is " +
            "the vulnerability");
        outcome.Error.Should().NotContain(
            "failed to run",
            "that wording means the spawn was attempted and the fake image happened to " +
            "be rejected by Windows, which is luck rather than a gate");
    }

    /// <summary>
    /// The positive case, first-class rather than an afterthought: an <b>unelevated
    /// per-user</b> upgrade whose <c>uninstall.exe</c> is unsigned and sits in the
    /// user's own profile — the ordinary shape of an unsigned per-user install — must
    /// NOT be gated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There is no privilege boundary in that run: the uninstaller executes with
    /// exactly the token of the user who owns the directory it sits in, so the check
    /// buys nothing an attacker could not do directly, while applying it made every
    /// such app permanently un-upgradable.
    /// </para>
    /// <para>
    /// "Not gated" is observed as the run REACHING <c>Process.Start</c> — the stub is
    /// not a valid Win32 image, so Windows rejects it and the engine reports "failed to
    /// run". That is the exact wording the test above forbids, which is what makes the
    /// pair jointly falsifiable: one asserts the gate fires, the other asserts it does
    /// not, and no single constant satisfies both.
    /// </para>
    /// </remarks>
    [WindowsFact("Windows-only elevation path")]
    public async Task An_unelevated_per_user_upgrade_of_an_unsigned_uninstaller_is_not_gated()
    {
        using var temp = new TempDir();
        var uninstaller = StubExe(temp, "uninstall.exe");

        var outcome = await RunUpgradeAsync(InstallScope.User, temp.Path, uninstaller);

        if (Elevation.IsProcessElevated())
        {
            outcome.Error.Should().Contain(
                "is not verified",
                "an ELEVATED per-user run IS a privilege boundary and stays gated");
        }
        else
        {
            outcome.Error.Should().Contain(
                "failed to run",
                "the run must have reached Process.Start — an unsigned per-user " +
                "uninstaller in the user's own profile is the ordinary shape of an " +
                "unsigned per-user install, and gating it makes that app permanently " +
                "un-upgradable for no security benefit whatsoever");
            outcome.Error.Should().NotContain("is not verified");
        }
    }

    /// <summary>
    /// The gate condition itself, as a full truth table of literals.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every expectation here is a <b>constant</b>. The earlier version wrote
    /// <c>.Should().Be(Elevation.IsProcessElevated())</c> — recomputing, in the
    /// expectation, the very thing the predicate read. That asserts only "the gate
    /// agrees with the token", which an <em>unconditional</em> gate also satisfies on
    /// an elevated host: the Critical could have been reverted and CI would have stayed
    /// green. Elevation is a parameter now precisely so the expectation can be a
    /// literal.
    /// </para>
    /// <para>
    /// The fourth row — <c>(User, unelevated) =&gt; false</c> — is the Critical's own
    /// branch, and it is pinned on every host, including an elevated runner.
    /// </para>
    /// </remarks>
    [WindowsTheory("Windows-only elevation path")]
    [InlineData(InstallScope.Machine, false, true)]
    [InlineData(InstallScope.Machine, true, true)]
    [InlineData(InstallScope.User, true, true)]
    [InlineData(InstallScope.User, false, false)]
    public void The_trust_gate_applies_to_machine_scope_and_to_any_elevated_run(
        InstallScope scope, bool elevated, bool expected)
    {
        InstallSession.PriorUninstallerNeedsTrust(scope, elevated).Should().Be(expected);
    }

    /// <summary>
    /// The predicate's refusal and acceptance, asserted directly so a regression to a
    /// constant — in either direction — cannot pass CI. No process is started: the
    /// point of a separable predicate is that the accepting branch is observable
    /// without running anything.
    /// </summary>
    [WindowsFact("Windows ACL APIs")]
    public void Prior_uninstaller_trust_accepts_an_admin_only_file_and_refuses_a_user_one()
    {
        using var temp = new TempDir();
        var userWritable = StubExe(temp, "uninstall.exe");

        // %TEMP% is the user's own directory and the stub carries no signature, so
        // every half of the predicate says no.
        InstallSession.IsPriorUninstallerTrusted(userWritable).Should().BeFalse();

        // System32\cmd.exe is TrustedInstaller-owned with no non-admin writer, on the
        // file AND its directory. It is only ever a path here — never started.
        File.Exists(SystemCmd).Should().BeTrue("the positive control needs a real system exe");
        InstallSession.IsPriorUninstallerTrusted(SystemCmd).Should().BeTrue(
            "a legitimate uninstaller lives under %ProgramFiles% / %SystemRoot%, and " +
            "refusing those would refuse every real machine-scope upgrade");

        InstallSession.IsPriorUninstallerTrusted(string.Empty).Should().BeFalse();
    }

    /// <summary>
    /// The file's OWN security descriptor is checked, not merely its folder's.
    /// <see cref="StateDirectorySecurity.IsAdminOnlyWritable"/> inspects the CONTAINING
    /// DIRECTORY, so an installer that ships a world-writable <c>uninstall.exe</c> into
    /// an admin-only directory passes a directory-only check while the attacker
    /// rewrites the file in place, never needing the directory at all.
    /// </summary>
    /// <remarks>
    /// Asserted the only way an unelevated session can: <c>System32\cmd.exe</c> is a
    /// real file in a real admin-only directory for which both predicates are true
    /// today, so pinning them beside a user-owned file for which the file predicate is
    /// false proves the file check is wired in and is not a constant in either
    /// direction. Nothing is written to <c>System32</c> — both calls are read-only ACL
    /// reads.
    /// </remarks>
    [WindowsFact("Windows ACL APIs")]
    public void The_uninstallers_own_acl_is_checked_not_just_its_directory()
    {
        using var temp = new TempDir();
        var userOwned = StubExe(temp, "uninstall.exe");

        StateDirectorySecurity.IsTrustedFile(SystemCmd).Should().BeTrue(
            "the file half must be satisfiable by a real system binary, or the gate is " +
            "a constant-false that refuses every legitimate upgrade");
        StateDirectorySecurity.IsAdminOnlyWritable(SystemCmd).Should().BeTrue();

        StateDirectorySecurity.IsTrustedFile(userOwned).Should().BeFalse(
            "a file the unprivileged user owns can be rewritten in place at any moment, " +
            "whatever its directory says");
        InstallSession.IsPriorUninstallerTrusted(userOwned).Should().BeFalse();
    }

    /// <summary>
    /// The discriminating case for the file check, and the one an unelevated session
    /// can actually construct: a path whose CONTAINING DIRECTORY is admin-only but
    /// whose FILE fails the file predicate. A directory-only gate answers <c>true</c>
    /// here; a gate that inspects the object itself answers <c>false</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The absent file is the stand-in. The case that matters in production is an
    /// <em>existing</em> <c>uninstall.exe</c> inside an admin-only directory carrying
    /// its own <c>Users:(M)</c> ACE — an installer that ships a world-writable binary
    /// into <c>%ProgramFiles%</c>. That fixture cannot be built without elevation (a
    /// directory this session can write to is by definition not admin-only), so it is
    /// listed in the report as an elevated check for gate G1. Both cases are refused by
    /// the same <c>IsTrustedFile</c> conjunct, and this one pins that conjunct is
    /// present and load-bearing.
    /// </para>
    /// <para>
    /// The engine checks <see cref="File.Exists(string)"/> before it reaches the gate,
    /// so this exact path is unreachable through <c>RunPriorUninstallAsync</c>; the
    /// predicate is therefore exercised directly, which is what the seam is for.
    /// </para>
    /// </remarks>
    [WindowsFact("Windows ACL APIs")]
    public void A_directory_only_check_would_accept_what_the_file_check_refuses()
    {
        var absentInSystem32 = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "sigil-test-" + Guid.NewGuid().ToString("N") + ".exe");

        File.Exists(absentInSystem32).Should().BeFalse(
            "the fixture must not exist — and nothing here creates it; System32 is only " +
            "ever read from in this file");

        StateDirectorySecurity.IsAdminOnlyWritable(absentInSystem32).Should().BeTrue(
            "IsAdminOnlyWritable answers for the CONTAINING DIRECTORY, which is System32");
        StateDirectorySecurity.IsTrustedFile(absentInSystem32).Should().BeFalse();

        InstallSession.IsPriorUninstallerTrusted(absentInSystem32).Should().BeFalse(
            "the gate must answer for the file it is about to spawn, not merely for the " +
            "folder that file sits in");
    }

    /// <summary>
    /// A remote path is refused outright. Both halves of the predicate are answered by
    /// the far end of a network hop — an SMB server reports the ACL, and
    /// <c>BUILTIN\Administrators</c> is a machine-independent SID, so a server the
    /// attacker controls can claim any owner and any DACL it likes. There is no
    /// legitimate case for an elevated installer launching a prior uninstaller off a
    /// share.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Asserted on the VERDICT, not on the bool</b>, and that is what makes it
    /// falsifiable. Every remote fixture below is also un-ACL-readable from an
    /// unprivileged process, so <c>IsPriorUninstallerTrusted</c> returns <c>false</c>
    /// for all of them whether or not the remote check exists — a bool assertion here
    /// passes with the refusal deleted. <c>Remote</c> versus <c>Untrusted</c> is the
    /// distinction only the refusal can produce, so deleting it turns these red.
    /// </para>
    /// <para>
    /// The hosts below are never contacted: classification is by path shape and the
    /// refusal short-circuits before any ACL or signature read. No network access.
    /// </para>
    /// </remarks>
    [WindowsTheory("Windows-only elevation path")]
    [InlineData(@"\\attacker\share\uninstall.exe")]
    [InlineData(@"\\127.0.0.1\C$\Windows\System32\cmd.exe")]
    [InlineData(@"\\?\UNC\attacker\share\uninstall.exe")]
    [InlineData(@"\\.\C:\Windows\System32\cmd.exe")]
    public void A_remote_uninstaller_path_is_refused(string path)
    {
        InstallSession.ClassifyPriorUninstaller(path).Should().Be(
            InstallSession.PriorUninstallerVerdict.Remote,
            "an SMB server the attacker controls reports whatever owner and DACL it " +
            "likes, and BUILTIN\\Administrators is a machine-independent SID — so this " +
            "must be refused on path SHAPE, before anything is read from it");

        InstallSession.IsPriorUninstallerTrusted(path).Should().BeFalse();
    }

    /// <summary>
    /// Non-vacuity for the test above, in both directions: a LOCAL path must not be
    /// classified <c>Remote</c> (or the refusal is a constant that breaks every
    /// legitimate upgrade), and a local path that fails on trust grounds must be
    /// reported as <c>Untrusted</c> rather than <c>Remote</c> (or the two verdicts are
    /// interchangeable and the test above proves nothing).
    /// </summary>
    [WindowsFact("Windows ACL APIs")]
    public void A_local_path_is_never_classified_remote_whatever_its_form()
    {
        using var temp = new TempDir();

        InstallSession.ClassifyPriorUninstaller(SystemCmd)
            .Should().Be(InstallSession.PriorUninstallerVerdict.Trusted);

        InstallSession.ClassifyPriorUninstaller(@"\\?\" + SystemCmd)
            .Should().Be(
                InstallSession.PriorUninstallerVerdict.Trusted,
                "the extended-length prefix on a local path is not a network hop");

        InstallSession.ClassifyPriorUninstaller(StubExe(temp, "uninstall.exe"))
            .Should().Be(
                InstallSession.PriorUninstallerVerdict.Untrusted,
                "a user-owned local file is refused on TRUST grounds, not as remote");
    }

    /// <summary><c>System32\cmd.exe</c> — used as a path only; never started.</summary>
    private static string SystemCmd => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

    /// <summary>A two-byte "MZ" stub: a real file, never a runnable image.</summary>
    private static string StubExe(TempDir temp, string name)
    {
        var path = Path.Combine(temp.Path, name);
        File.WriteAllBytes(path, new byte[] { 0x4D, 0x5A });
        return path;
    }

    /// <summary>
    /// Drive the real <see cref="InstallSession.RunInstallCoreAsync"/> upgrade teardown
    /// with an injected installed state and an empty payload, so the run reaches the
    /// prior-version teardown and aborts there — before the journal is ever opened and
    /// before anything is written to disk.
    /// </summary>
    /// <remarks>
    /// This is what the existing <c>UpgradeSessionTests</c> do not do: they all inject
    /// a non-existent <c>PriorUninstallExe</c>, so every one of them exits at the
    /// earlier <c>File.Exists</c> branch and none has ever reached the gate. That is
    /// why an unconditional gate broke unsigned per-user upgrades undetected.
    /// </remarks>
    private static async Task<InstallOutcome> RunUpgradeAsync(
        InstallScope scope, string priorInstallDir, string priorUninstallExe)
    {
        var blob = Blob("2.0.0", scope);
        var session = InstallSession.ForTesting(
            blob,
            CommandLineParser.Parse(new[] { "/silent" }, blob.Parameters),
            new UpgradeState(
                Found: true,
                InstalledVersion: "1.0.0",
                PriorInstallDir: priorInstallDir,
                PriorUninstallExe: priorUninstallExe,
                FoundScope: scope));

        session.UpgradeAction.Should().Be(
            UpgradeAction.Upgrade, "the teardown under test only runs for an upgrade");
        session.ResolvedScope.Should().Be(scope);

        var outcome = await session.RunInstallCoreAsync(Array.Empty<byte>(), progress: null);
        outcome.Success.Should().BeFalse(
            "the stub is not a runnable image, so the run aborts either at the gate or " +
            "at Process.Start — the WHICH is what each caller asserts");
        return outcome;
    }

    private static WrapperBlob Blob(string version, InstallScope scope) => new(
        AppId: "sigil.test.prior-uninstaller",
        Parameters: Array.Empty<ParameterDefinition>(),
        InstallSteps: Array.Empty<InstallStep>(),
        PreInstall: Array.Empty<InstallStep>(),
        PostInstall: Array.Empty<InstallStep>(),
        UpdateSteps: Array.Empty<InstallStep>(),
        Scope: scope,
        Version: version);
}
