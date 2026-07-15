using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Cli;
using SigilBuild.Wrapper.Engine;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Engine;

/// <summary>
/// P3 (gap G3): the version-aware paths wired through <see cref="InstallSession"/>,
/// driven with an INJECTED installed state (no real registry / process launch) via
/// the <c>ForTesting</c> seam.
/// </summary>
public sealed class UpgradeSessionTests
{
    private static WrapperBlob Blob(string version, InstallScope scope = InstallScope.Auto) => new(
        AppId: "com.acme.Studio",
        Parameters: Array.Empty<ParameterDefinition>(),
        InstallSteps: Array.Empty<InstallStep>(),
        PreInstall: Array.Empty<InstallStep>(),
        PostInstall: Array.Empty<InstallStep>(),
        UpdateSteps: Array.Empty<InstallStep>(),
        Scope: scope,
        Version: version);

    private static UpgradeState Installed(
        string version, InstallScope scope = InstallScope.User, string? uninstallExe = null) =>
        new(Found: true, InstalledVersion: version,
            PriorInstallDir: @"C:\Apps\Acme",
            PriorUninstallExe: uninstallExe ?? @"C:\Apps\Acme\uninstall.exe",
            FoundScope: scope);

    private static InstallSession Session(WrapperBlob blob, UpgradeState state, params string[] args)
    {
        var parsed = CommandLineParser.Parse(args, blob.Parameters);
        return InstallSession.ForTesting(blob, parsed, state);
    }

    [Fact]
    public void Fresh_when_nothing_installed()
    {
        var session = Session(Blob("2.0.0"), UpgradeState.None, "/silent");
        session.UpgradeAction.Should().Be(UpgradeAction.Fresh);
        session.IsDowngradeBlocked.Should().BeFalse();
    }

    [Fact]
    public void Older_installed_classifies_as_upgrade()
    {
        var session = Session(Blob("2.0.0"), Installed("1.0.0"), "/silent");
        session.UpgradeAction.Should().Be(UpgradeAction.Upgrade);
        session.InstalledVersion.Should().Be("1.0.0");
    }

    [Fact]
    public async Task Newer_installed_silent_blocks_with_exit_code_3()
    {
        var session = Session(Blob("1.0.0"), Installed("2.0.0"), "/silent");
        session.IsDowngradeBlocked.Should().BeTrue();

        var output = new StringWriter();
        var error = new StringWriter();
        var code = await session.RunHeadlessAsync(output, error);

        code.Should().Be(InstallSession.DowngradeBlockedExitCode);
        code.Should().Be(3);
        error.ToString().Should().Contain("newer version");
    }

    [Fact]
    public void Force_downgrade_flag_turns_a_block_into_a_forced_downgrade()
    {
        var session = Session(Blob("1.0.0"), Installed("2.0.0"), "/silent", "/force-downgrade");
        session.UpgradeAction.Should().Be(UpgradeAction.DowngradeForced);
        session.IsDowngradeBlocked.Should().BeFalse();
    }

    [Fact]
    public void Existing_install_scope_wins_over_auto_resolution()
    {
        // Manifest auto (→ user by default), but the prior install lives in the
        // machine hive: the existing scope wins so the upgrade re-targets machine.
        var session = Session(Blob("2.0.0", InstallScope.Auto), Installed("1.0.0", InstallScope.Machine), "/silent");
        session.ResolvedScope.Should().Be(InstallScope.Machine);
    }

    [Fact]
    public void Explicit_scope_flag_is_not_overridden_by_existing_install_scope()
    {
        // /currentuser is authoritative even though the prior install is machine-scoped.
        var session = Session(Blob("2.0.0", InstallScope.Auto), Installed("1.0.0", InstallScope.Machine), "/silent", "/currentuser");
        session.ResolvedScope.Should().Be(InstallScope.User);
    }

    [Fact]
    public void Same_scope_upgrade_defaults_the_destination_to_the_prior_install_dir()
    {
        var state = new UpgradeState(Found: true, InstalledVersion: "1.0.0",
            PriorInstallDir: @"C:\Apps\Acme", PriorUninstallExe: @"C:\Apps\Acme\uninstall.exe",
            FoundScope: InstallScope.User);
        // Auto manifest, no flag → resolves user; prior install is user → same scope.
        var session = Session(Blob("2.0.0", InstallScope.Auto), state, "/silent");

        session.ResolveDefaultInstallDir().Should().Be(@"C:\Apps\Acme", "an in-place upgrade keeps the prior dir");
    }

    [Fact]
    public void Cross_scope_reinstall_does_not_default_to_the_other_scopes_prior_dir()
    {
        // Prior install is per-user; the user promotes to machine with /allusers.
        // The per-user prior dir must NOT be the default for a machine install.
        var state = new UpgradeState(Found: true, InstalledVersion: "1.0.0",
            PriorInstallDir: @"C:\Users\me\AppData\Local\Programs\Acme",
            PriorUninstallExe: @"C:\Users\me\AppData\Local\Programs\Acme\uninstall.exe",
            FoundScope: InstallScope.User);
        var session = Session(Blob("2.0.0", InstallScope.Auto), state, "/silent", "/allusers");

        session.ResolvedScope.Should().Be(InstallScope.Machine);
        session.ResolveDefaultInstallDir().Should().NotBe(
            @"C:\Users\me\AppData\Local\Programs\Acme",
            "a scope change installs into the new scope's default, not the other scope's prior dir");
    }

    [Fact]
    public async Task Upgrade_with_missing_prior_uninstaller_fails_without_partial_install()
    {
        var missing = Path.Combine(Path.GetTempPath(), "sigil-no-such-" + Guid.NewGuid().ToString("N"), "uninstall.exe");
        var session = Session(Blob("2.0.0"), Installed("1.0.0", uninstallExe: missing), "/silent");
        session.UpgradeAction.Should().Be(UpgradeAction.Upgrade);

        // Empty payload (un-stamped test host): the run reaches the pre-body uninstall
        // phase, which fails before the journal is ever opened.
        var outcome = await session.RunInstallAsync(progress: null, CancellationToken.None);

        outcome.Success.Should().BeFalse();
        outcome.Error.Should().Contain("uninstaller was not found");
    }
}
