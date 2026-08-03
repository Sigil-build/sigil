using System.IO;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Engine;

/// <summary>
/// Pins the <c>{install_dir}</c> default + override precedence and the
/// <c>{scope_root}</c> / <c>{app.*}</c> token resolution (T13).
/// </summary>
/// <remarks>
/// The precedence cases below deliberately use arbitrary absolute paths
/// (<c>C:\Tools\Acme</c>, <c>D:\Existing\Acme</c>) to make "which source won"
/// unmistakable. Since R3 those paths are outside the scope root, so they pass
/// the <c>allowAnyRoot</c> escape hatch — these fixtures test PRECEDENCE, not
/// containment; containment has its own suite in
/// <see cref="InstallDirContainmentTests"/>. The production rule is untouched:
/// the hatch is <c>internal</c> and no <c>src/</c> path can reach it.
/// </remarks>
public sealed class InstallDirResolverTests
{
    private static string UserRoot => ScopeLayout.For(InstallScope.User).InstallRoot;
    private static string MachineRoot => ScopeLayout.For(InstallScope.Machine).InstallRoot;

    [Fact]
    public void Default_is_scope_root_joined_with_app_name()
    {
        var resolved = InstallDirResolver.Resolve(
            InstallScope.User, appName: "Acme Studio", appId: "com.acme.Studio",
            manifestInstallDir: null, cliOverride: null);

        resolved.Should().Be(Path.Combine(UserRoot, "Acme Studio"));
    }

    [Fact]
    public void Default_reflects_scope_root_for_machine()
    {
        var resolved = InstallDirResolver.Resolve(
            InstallScope.Machine, appName: "Acme Studio", appId: "com.acme.Studio",
            manifestInstallDir: null, cliOverride: null);

        resolved.Should().Be(Path.Combine(MachineRoot, "Acme Studio"));
    }

    [Fact]
    public void Manifest_override_resolves_scope_root_token()
    {
        // The reference manifest form: install_dir: "{scope_root}/Acme Studio".
        var resolved = InstallDirResolver.Resolve(
            InstallScope.User, appName: "Acme Studio", appId: "com.acme.Studio",
            manifestInstallDir: "{scope_root}/Acme Studio", cliOverride: null);

        resolved.Should().Be(Path.Combine(UserRoot, "Acme Studio"));
    }

    [Fact]
    public void Manifest_override_resolves_app_name_and_id_tokens()
    {
        var byName = InstallDirResolver.Resolve(
            InstallScope.Machine, appName: "Acme Studio", appId: "com.acme.Studio",
            manifestInstallDir: "{scope_root}/{app.name}", cliOverride: null);
        byName.Should().Be(Path.Combine(MachineRoot, "Acme Studio"));

        var byId = InstallDirResolver.Resolve(
            InstallScope.Machine, appName: "Acme Studio", appId: "com.acme.Studio",
            manifestInstallDir: "{scope_root}/{app.id}", cliOverride: null);
        byId.Should().Be(Path.Combine(MachineRoot, "com.acme.Studio"));
    }

    [Fact]
    public void D_override_wins_over_manifest_and_default()
    {
        var target = Path.Combine("C:", "Tools", "Acme");
        var resolved = InstallDirResolver.Resolve(
            InstallScope.User, appName: "Acme Studio", appId: "com.acme.Studio",
            manifestInstallDir: "{scope_root}/Acme Studio", cliOverride: target,
            allowAnyRoot: true);

        resolved.Should().Be(Path.GetFullPath(target));
    }

    [Fact]
    public void Collected_wizard_path_wins_over_cli_override()
    {
        var cli = Path.Combine("C:", "FromCli");
        var wizard = Path.Combine("C:", "FromWizard");

        var resolved = InstallDirResolver.Resolve(
            InstallScope.User, appName: "Acme", appId: "id",
            manifestInstallDir: null, cliOverride: cli, allowAnyRoot: true, collected: wizard);

        resolved.Should().Be(Path.GetFullPath(wizard));
    }

    // ── Prior install dir (P3 upgrade) precedence ─────────────────────────────

    [Fact]
    public void Prior_install_dir_wins_over_manifest_and_default()
    {
        var prior = Path.Combine("D:", "Existing", "Acme");
        var resolved = InstallDirResolver.Resolve(
            InstallScope.User, appName: "Acme Studio", appId: "com.acme.Studio",
            manifestInstallDir: "{scope_root}/Acme Studio", cliOverride: null,
            allowAnyRoot: true, collected: null, priorInstallDir: prior);

        resolved.Should().Be(Path.GetFullPath(prior));
    }

    [Fact]
    public void Explicit_D_override_wins_over_prior_install_dir()
    {
        var prior = Path.Combine("D:", "Existing", "Acme");
        var cli = Path.Combine("C:", "Chosen");
        var resolved = InstallDirResolver.Resolve(
            InstallScope.User, appName: "Acme", appId: "id",
            manifestInstallDir: null, cliOverride: cli,
            allowAnyRoot: true, collected: null, priorInstallDir: prior);

        resolved.Should().Be(Path.GetFullPath(cli));
    }

    [Fact]
    public void Collected_wizard_path_wins_over_prior_install_dir()
    {
        var prior = Path.Combine("D:", "Existing", "Acme");
        var wizard = Path.Combine("C:", "FromWizard");
        var resolved = InstallDirResolver.Resolve(
            InstallScope.User, appName: "Acme", appId: "id",
            manifestInstallDir: null, cliOverride: null,
            allowAnyRoot: true, collected: wizard, priorInstallDir: prior);

        resolved.Should().Be(Path.GetFullPath(wizard));
    }

    [Fact]
    public void Blank_app_name_falls_back_to_app_id()
    {
        var resolved = InstallDirResolver.Resolve(
            InstallScope.User, appName: "  ", appId: "com.acme.Studio",
            manifestInstallDir: null, cliOverride: null);

        resolved.Should().Be(Path.Combine(UserRoot, "com.acme.Studio"));
    }
}
