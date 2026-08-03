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

    [WindowsFact("Windows scope roots")]
    public void Machine_scope_rejects_an_out_of_root_prior_install_dir()
    {
        var act = () => InstallDirResolver.Resolve(
            InstallScope.Machine,
            appName: "MyApp",
            appId: "com.example.myapp",
            manifestInstallDir: null,
            cliOverride: null,
            collected: null,
            priorInstallDir: @"C:\Users\Public\evil");

        act.Should().Throw<InstallDirRejectedException>().WithMessage("*outside*");
    }

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

    private static WrapperBlob EvilBlob() => new(
        AppId: "com.example.myapp",
        Parameters: Array.Empty<ParameterDefinition>(),
        InstallSteps: Array.Empty<InstallStep>(),
        PreInstall: Array.Empty<InstallStep>(),
        PostInstall: Array.Empty<InstallStep>(),
        UpdateSteps: Array.Empty<InstallStep>(),
        AppName: "MyApp",
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
