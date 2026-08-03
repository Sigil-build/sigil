namespace SigilBuild.Wrapper.Tests.Engine;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Cli;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Tests.Helpers;

/// <summary>
/// Register row R3: <c>/D=</c> was accepted unvalidated, and <c>{install_dir}</c>
/// substitutes into <c>scheduled_task_create.program</c> / <c>service_install.binary_path</c>
/// — both SYSTEM-level. Every <c>install_dir</c> source must be contained to the
/// scope root, not just <c>/D=</c>.
/// </summary>
public sealed class InstallDirContainmentTests
{
    private const string JunctionPrefix = "sigil-s2-junction-";

    private static string MachineRoot => ScopeLayout.For(InstallScope.Machine).InstallRoot;

    [WindowsFact("Windows scope roots")]
    public void Machine_scope_rejects_a_cli_override_outside_the_scope_root()
    {
        // Setup.exe /allusers /D=C:\Users\Public\evil — an admin approves the
        // UAC prompt for a legitimately signed installer, and a SYSTEM-level
        // scheduled task or service then points at a user-writable directory.
        var act = () => InstallDirResolver.Resolve(
            InstallScope.Machine,
            appName: "MyApp",
            appId: "com.example.myapp",
            manifestInstallDir: null,
            cliOverride: @"C:\Users\Public\evil");

        act.Should().Throw<InstallDirRejectedException>()
           .WithMessage("*outside*");
    }

    [WindowsFact("Windows scope roots")]
    public void Machine_scope_accepts_a_path_under_the_scope_root()
    {
        var resolved = InstallDirResolver.Resolve(
            InstallScope.Machine,
            appName: "MyApp",
            appId: "com.example.myapp",
            manifestInstallDir: null,
            cliOverride: Path.Combine(MachineRoot, "MyApp"));

        resolved.Should().StartWith(MachineRoot);
    }

    // ── Every source, not just /D= ────────────────────────────────────────────
    // A manifest author pointing at C:\Users\Public is the same hole, and so is
    // a recovered prior install dir (R1's neighbour) or a wizard-collected path.

    [WindowsFact("Windows scope roots")]
    public void Machine_scope_rejects_an_out_of_root_manifest_install_dir()
    {
        var act = () => InstallDirResolver.Resolve(
            InstallScope.Machine,
            appName: "MyApp",
            appId: "com.example.myapp",
            manifestInstallDir: @"C:\Users\Public\evil",
            cliOverride: null);

        act.Should().Throw<InstallDirRejectedException>().WithMessage("*outside*");
    }

    [WindowsFact("Windows scope roots")]
    public void Machine_scope_rejects_an_out_of_root_wizard_collected_path()
    {
        var act = () => InstallDirResolver.Resolve(
            InstallScope.Machine,
            appName: "MyApp",
            appId: "com.example.myapp",
            manifestInstallDir: null,
            cliOverride: null,
            collected: @"C:\Users\Public\evil");

        act.Should().Throw<InstallDirRejectedException>().WithMessage("*outside*");
    }

    // NOTE: an out-of-root `priorInstallDir` was refused in the first cut of this
    // lane. That was reversed by ruling — see the grandfather-clause section
    // below. A recovered prior directory is not attacker-supplied input, and
    // refusing it stranded installs that predate containment.

    [WindowsFact("Windows scope roots")]
    public void Machine_scope_rejects_a_traversal_escape_from_the_scope_root()
    {
        var act = () => InstallDirResolver.Resolve(
            InstallScope.Machine,
            appName: "MyApp",
            appId: "com.example.myapp",
            manifestInstallDir: null,
            cliOverride: Path.Combine(MachineRoot, "..", "Users", "Public", "evil"));

        act.Should().Throw<InstallDirRejectedException>().WithMessage("*outside*");
    }

    // ── User scope ────────────────────────────────────────────────────────────

    [WindowsFact("Windows scope roots")]
    public void User_scope_default_and_profile_paths_are_accepted()
    {
        var userRoot = ScopeLayout.For(InstallScope.User).InstallRoot;

        var byDefault = InstallDirResolver.Resolve(
            InstallScope.User, appName: "MyApp", appId: "com.example.myapp",
            manifestInstallDir: null, cliOverride: null);
        byDefault.Should().Be(Path.Combine(userRoot, "MyApp"));

        var inProfile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Apps", "MyApp");
        InstallDirResolver.Resolve(
            InstallScope.User, appName: "MyApp", appId: "com.example.myapp",
            manifestInstallDir: null, cliOverride: inProfile)
            .Should().Be(inProfile);
    }

    [WindowsFact("Windows scope roots")]
    public void User_scope_rejects_a_path_outside_the_users_own_root()
    {
        var act = () => InstallDirResolver.Resolve(
            InstallScope.User,
            appName: "MyApp",
            appId: "com.example.myapp",
            manifestInstallDir: null,
            cliOverride: @"C:\Windows\Temp\evil");

        act.Should().Throw<InstallDirRejectedException>().WithMessage("*outside*");
    }

    // ── Neither entry path may let the refusal escape unhandled ───────────────

    private static WrapperBlob EvilBlob(string appName = "MyApp") => new(
        AppId: "com.example.myapp",
        Parameters: Array.Empty<ParameterDefinition>(),
        InstallSteps: Array.Empty<InstallStep>(),
        PreInstall: Array.Empty<InstallStep>(),
        PostInstall: Array.Empty<InstallStep>(),
        UpdateSteps: Array.Empty<InstallStep>(),
        AppName: appName,
        Scope: InstallScope.User,
        InstallDir: @"C:\Windows\Temp\evil");

    [WindowsFact("Windows scope roots")]
    public async Task Silent_path_reports_the_refusal_as_a_failure_not_a_crash()
    {
        var blob = EvilBlob();
        var session = InstallSession.ForTesting(
            blob, CommandLineParser.Parse(new[] { "/silent" }, blob.Parameters));

        var output = new StringWriter();
        var error = new StringWriter();
        var code = await session.RunHeadlessAsync(output, error);

        code.Should().Be(1, "a refused install_dir is a plain failure, not an unhandled exception");
        error.ToString().Should().Contain("outside");
    }

    [WindowsFact("Windows scope roots")]
    public async Task Wizard_path_reports_the_refusal_as_a_failed_outcome()
    {
        var blob = EvilBlob();
        var session = InstallSession.ForTesting(
            blob, CommandLineParser.Parse(Array.Empty<string>(), blob.Parameters));

        // The wizard's pre-fill runs before any window exists (App.axaml.cs), so
        // it must never throw — it falls back to the scope default and logs the
        // refusal instead of taking the process down.
        var prefill = session.ResolveDefaultInstallDir(InstallScope.User);
        prefill.Should().Be(Path.Combine(ScopeLayout.For(InstallScope.User).InstallRoot, "MyApp"));

        // The run itself still refuses, and surfaces it as a Failed-screen outcome.
        session.CollectedInstallDir = @"C:\Windows\Temp\evil";
        var outcome = await session.RunInstallAsync(progress: null, CancellationToken.None);

        outcome.Success.Should().BeFalse();
        outcome.Error.Should().Contain("outside");
    }

    [WindowsFact("Windows directory junctions")]
    public void Wizard_prefill_does_not_rethrow_when_the_scope_default_is_itself_a_junction()
    {
        // Fix round 1, Important 2. The fallback inside
        // catch (InstallDirRejectedException) used to call the CHECKING overload
        // again. If <InstallRoot>\<AppName> is itself a junction that second call
        // also rejects — and the exception escapes the very catch written to stop
        // the first one. App.axaml.cs has no try/catch, so the wizard would die
        // with no window at all.
        var installRoot = ScopeLayout.For(InstallScope.User).InstallRoot;
        Directory.CreateDirectory(installRoot);

        // A hard kill between CreateOrFail and the finally below would strand a
        // link here, and a stray junction under Programs looks like an installed
        // app. Sweep any predecessor so repeated runs cannot accumulate them.
        Junction.SweepStale(installRoot, JunctionPrefix);

        var appName = JunctionPrefix + Guid.NewGuid().ToString("N");
        var scopeDefault = Path.Combine(installRoot, appName);
        using var outside = new TempDir();
        Junction.CreateOrFail(scopeDefault, outside.Path);

        try
        {
            // The manifest install_dir is out of root, so the pre-fill takes the
            // fallback; the fallback's own target is the junction above.
            var blob = EvilBlob(appName);
            var session = InstallSession.ForTesting(
                blob, CommandLineParser.Parse(Array.Empty<string>(), blob.Parameters));

            var act = () => session.ResolveDefaultInstallDir(InstallScope.User);

            act.Should().NotThrow("the pre-fill runs before any window exists");
            act().Should().Be(scopeDefault, "the scope default is a display value, resolved unchecked");
        }
        finally
        {
            Junction.Remove(scopeDefault);
        }
    }

    // ── Ruling 1: contain new installs, GRANDFATHER prior ones ────────────────
    //
    // An install that already lives outside the scope root predates R3. Refusing
    // it strands the user with an app that can be neither upgraded nor cleanly
    // removed — worse than the hole it closes, and a prior install dir is not
    // attacker-supplied input. But the exemption must not become a bypass.

    private const string OutOfRootPriorDir = @"C:\Apps\Acme";

    private static WrapperBlob UpgradeBlob() => new(
        AppId: "com.acme.Studio",
        Parameters: Array.Empty<ParameterDefinition>(),
        InstallSteps: Array.Empty<InstallStep>(),
        PreInstall: Array.Empty<InstallStep>(),
        PostInstall: Array.Empty<InstallStep>(),
        UpdateSteps: Array.Empty<InstallStep>(),
        AppName: "Acme",
        Scope: InstallScope.User,
        Version: "2.0.0");

    private static UpgradeState PriorOutOfRoot() => new(
        Found: true,
        InstalledVersion: "1.0.0",
        PriorInstallDir: OutOfRootPriorDir,
        PriorUninstallExe: Path.Combine(OutOfRootPriorDir, "uninstall.exe"),
        FoundScope: InstallScope.User);

    [WindowsFact("Windows scope roots")]
    public void Out_of_root_prior_install_dir_is_honoured()
    {
        // The grandfather clause: no /D=, no wizard pick — the recovered prior
        // directory wins the precedence and is let through.
        var resolved = InstallDirResolver.Resolve(
            InstallScope.User,
            appName: "Acme",
            appId: "com.acme.Studio",
            manifestInstallDir: null,
            cliOverride: null,
            collected: null,
            priorInstallDir: OutOfRootPriorDir);

        resolved.Should().Be(OutOfRootPriorDir,
            "an install predating containment must stay upgradable and cleanly removable");
    }

    [WindowsFact("Windows scope roots")]
    public async Task Out_of_root_prior_install_dir_is_logged_loudly()
    {
        using var tmp = new TempDir();
        var logPath = Path.Combine(tmp.Path, "grandfather.log");

        var blob = UpgradeBlob();
        var session = InstallSession.ForTesting(
            blob,
            CommandLineParser.Parse(new[] { "/silent", $"/LOG={logPath}" }, blob.Parameters),
            PriorOutOfRoot());

        session.ResolveDefaultInstallDir().Should().Be(OutOfRootPriorDir);

        // The run itself fails later (the fixture's prior uninstaller does not
        // exist); the exemption is recorded before any of that.
        await session.RunHeadlessAsync(new StringWriter(), new StringWriter());

        var log = File.ReadAllText(logPath);
        log.Should().Contain("honouring the prior install directory");
        log.Should().Contain(OutOfRootPriorDir);
        log.Should().Contain("OUTSIDE", "a quiet exemption is how the exemption becomes the norm");
        log.Should().Contain("A NEW destination outside the root is still refused.");
    }

    [WindowsFact("Windows scope roots")]
    public void Out_of_root_cli_override_is_still_refused_when_a_prior_install_exists()
    {
        // THE BYPASS TEST. The grandfather clause is gated on the prior dir
        // WINNING the precedence — not on a prior install merely existing. A /D=
        // outranks it, so this is a new attacker-reachable destination and stays
        // refused even though an out-of-root prior install is recorded.
        var act = () => InstallDirResolver.Resolve(
            InstallScope.User,
            appName: "Acme",
            appId: "com.acme.Studio",
            manifestInstallDir: null,
            cliOverride: @"C:\Users\Public\evil",
            collected: null,
            priorInstallDir: OutOfRootPriorDir);

        act.Should().Throw<InstallDirRejectedException>().WithMessage("*outside*");
    }

    [WindowsFact("Windows scope roots")]
    public void Out_of_root_wizard_pick_is_still_refused_when_a_prior_install_exists()
    {
        // Same bypass, the other attacker-reachable source.
        var act = () => InstallDirResolver.Resolve(
            InstallScope.User,
            appName: "Acme",
            appId: "com.acme.Studio",
            manifestInstallDir: null,
            cliOverride: null,
            collected: @"C:\Users\Public\evil",
            priorInstallDir: OutOfRootPriorDir);

        act.Should().Throw<InstallDirRejectedException>().WithMessage("*outside*");
    }

    [WindowsFact("Windows scope roots")]
    public async Task Silent_run_still_refuses_an_out_of_root_D_despite_a_prior_install()
    {
        var blob = UpgradeBlob();
        var session = InstallSession.ForTesting(
            blob,
            CommandLineParser.Parse(new[] { "/silent", @"/D=C:\Users\Public\evil" }, blob.Parameters),
            PriorOutOfRoot());

        var error = new StringWriter();
        var code = await session.RunHeadlessAsync(new StringWriter(), error);

        code.Should().Be(1);
        error.ToString().Should().Contain("outside");
    }

    // ── Ruling 2: both Program Files roots are valid machine anchors ───────────

    [WindowsFact("Windows scope roots")]
    public void Machine_scope_accepts_the_x86_program_files_root()
    {
        var x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        x86.Should().NotBeNullOrWhiteSpace("64-bit Windows always exposes the x86 Program Files root");

        var target = Path.Combine(x86, "MyApp");
        var resolved = InstallDirResolver.Resolve(
            InstallScope.Machine,
            appName: "MyApp",
            appId: "com.example.myapp",
            manifestInstallDir: null,
            cliOverride: target);

        resolved.Should().Be(target,
            "both Program Files roots are admin-only and TrustedInstaller-owned, " +
            "and refusing x86 would break the standard 32-bit install shape");
    }

    [WindowsTheory("Windows scope roots")]
    [InlineData(@"C:\Users\Public\evil")]
    [InlineData(@"C:\ProgramData\evil")]
    [InlineData(@"C:\Windows\Tracing\evil")]
    public void Machine_scope_still_refuses_user_writable_roots(string target)
    {
        // Widening to two Program Files roots must not have widened anything
        // else. Each of these is writable by a non-administrator (ProgramData
        // grants BUILTIN\Users:(CI)(WD,AD,WEA,WA); Windows\Tracing grants
        // BUILTIN\Users:(RX,W)), which is exactly R3's escalation.
        var act = () => InstallDirResolver.Resolve(
            InstallScope.Machine,
            appName: "MyApp",
            appId: "com.example.myapp",
            manifestInstallDir: null,
            cliOverride: target);

        act.Should().Throw<InstallDirRejectedException>().WithMessage("*outside*");
    }

    // ── The test-only escape hatch ────────────────────────────────────────────

    [WindowsFact("Windows scope roots")]
    public void AllowAnyRoot_is_the_test_only_escape_hatch()
    {
        // Existing fixtures legitimately resolve to arbitrary temp paths. The
        // production rule is not weakened for them — they opt out explicitly.
        InstallDirResolver.Resolve(
            InstallScope.Machine, appName: "MyApp", appId: "com.example.myapp",
            manifestInstallDir: null, cliOverride: @"C:\Users\Public\evil",
            allowAnyRoot: true)
            .Should().Be(@"C:\Users\Public\evil");
    }
}
