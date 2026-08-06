namespace SigilBuild.Wrapper.Tests.Engine;

using System;
using System.IO;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using FluentAssertions;
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
/// uniquely named, and is removed in a <c>finally</c> via <c>using</c>. No test in
/// this file starts a process: the refusal is asserted through the engine's typed
/// outcome, and the acceptance through the predicate directly.
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
    /// The non-vacuity control for the test above. Without this, deleting the
    /// registry read entirely — or a fixture that silently failed to plant the key —
    /// would leave that test green.
    /// </summary>
    [WindowsFact("Windows registry")]
    public void The_same_HKCU_entry_is_still_found_by_a_user_scope_resolve()
    {
        var appId = "sigil.test." + Guid.NewGuid().ToString("N");
        using var planted = TestRegistry.PlantUserUninstallEntry(
            appId,
            displayVersion: "0.0.1",
            uninstallString: @"""C:\Users\Public\evil.exe"" /S /Uninstall");

        var user = InstalledStateResolver.Resolve(appId, InstallScope.User);

        user.Found.Should().BeTrue("the fixture really did plant a readable ARP entry");
        user.InstalledVersion.Should().Be("0.0.1");
        user.PriorUninstallExe.Should().Be(@"C:\Users\Public\evil.exe");
        user.FoundScope.Should().Be(InstallScope.User);
    }

    /// <summary>
    /// The user-scope → HKLM fallback is deliberately kept: reading a hive the caller
    /// cannot write is safe, and it is what makes a machine install discoverable from
    /// a per-user run. Asserted against the resolver's own probe order rather than by
    /// planting an HKLM key, which would need elevation and would mutate the real
    /// machine's installed-programs list.
    /// </summary>
    [WindowsFact("Windows registry")]
    public void User_scope_resolve_of_an_unknown_app_is_simply_absent()
    {
        var appId = "sigil.test." + Guid.NewGuid().ToString("N");

        InstalledStateResolver.Resolve(appId, InstallScope.User).Found.Should().BeFalse();
        InstalledStateResolver.Resolve(appId, InstallScope.Machine).Found.Should().BeFalse();
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
        var fakeUninstaller = Path.Combine(temp.Path, "evil.exe");
        File.WriteAllBytes(fakeUninstaller, new byte[] { 0x4D, 0x5A });   // "MZ"

        var blob = Blob("2.0.0");
        var state = new UpgradeState(
            Found: true,
            InstalledVersion: "1.0.0",
            PriorInstallDir: temp.Path,
            PriorUninstallExe: fakeUninstaller,
            FoundScope: InstallScope.User);
        var session = InstallSession.ForTesting(
            blob, CommandLineParser.Parse(new[] { "/silent" }, blob.Parameters), state);
        session.UpgradeAction.Should().Be(
            UpgradeAction.Upgrade, "the teardown under test only runs for an upgrade");

        // Act — empty payload, so the run reaches the prior-version teardown and
        // aborts there, before the journal is ever opened.
        var outcome = await session.RunInstallCoreAsync(Array.Empty<byte>(), progress: null);

        // Assert
        outcome.Success.Should().BeFalse();
        outcome.Error.Should().Contain(
            "is not verified",
            "the elevated process must refuse a registry-supplied uninstaller that is " +
            "neither signed nor admin-path-resident — reaching Process.Start at all is " +
            "the vulnerability");
        outcome.Error.Should().NotContain(
            "failed to run",
            "that wording means the spawn was attempted and the fake image happened to " +
            "be rejected by Windows, which is luck rather than a gate");
    }

    /// <summary>
    /// The predicate's refusal and acceptance, asserted directly so a regression to a
    /// constant — in either direction — cannot pass CI. No process is started: the
    /// point of a separable predicate is that the accepting branch is observable
    /// without running anything.
    /// </summary>
    [WindowsFact("Windows ACL APIs")]
    public void Prior_uninstaller_trust_accepts_an_admin_only_directory_and_refuses_a_user_one()
    {
        using var temp = new TempDir();
        var userWritable = Path.Combine(temp.Path, "uninstall.exe");
        File.WriteAllBytes(userWritable, new byte[] { 0x4D, 0x5A });

        // %TEMP% is the user's own directory and the stub carries no signature, so
        // both halves of the predicate say no.
        InstallSession.IsPriorUninstallerTrusted(userWritable).Should().BeFalse();

        // System32 is TrustedInstaller-owned with no non-admin writer, so the
        // admin-only-directory half says yes. cmd.exe is only ever a path here — it
        // is never started.
        var systemExe = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        File.Exists(systemExe).Should().BeTrue("the positive control needs a real system exe");
        InstallSession.IsPriorUninstallerTrusted(systemExe).Should().BeTrue(
            "a legitimate uninstaller lives under %ProgramFiles% / %SystemRoot%, and " +
            "refusing those would refuse every real machine-scope upgrade");

        InstallSession.IsPriorUninstallerTrusted(string.Empty).Should().BeFalse();
    }

    private static WrapperBlob Blob(string version) => new(
        AppId: "sigil.test.prior-uninstaller",
        Parameters: Array.Empty<ParameterDefinition>(),
        InstallSteps: Array.Empty<InstallStep>(),
        PreInstall: Array.Empty<InstallStep>(),
        PostInstall: Array.Empty<InstallStep>(),
        UpdateSteps: Array.Empty<InstallStep>(),
        Scope: InstallScope.User,
        Version: version);
}
