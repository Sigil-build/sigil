using System;
using System.Collections.Generic;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Cli;
using SigilBuild.Wrapper.Engine;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Engine;

/// <summary>
/// T12 scope threading through the shared <see cref="InstallSession"/> driver and
/// the exposure of the resolved scope to the expression engine.
/// </summary>
public sealed class ScopeInstallSessionTests
{
    private static WrapperBlob BlobWithScope(InstallScope scope) => new(
        AppId: "com.acme.Studio",
        Parameters: Array.Empty<ParameterDefinition>(),
        InstallSteps: Array.Empty<InstallStep>(),
        PreInstall: Array.Empty<InstallStep>(),
        PostInstall: Array.Empty<InstallStep>(),
        UpdateSteps: Array.Empty<InstallStep>(),
        Scope: scope);

    private static InstallSession Session(InstallScope manifestScope, params string[] args)
    {
        var blob = BlobWithScope(manifestScope);
        var parsed = CommandLineParser.Parse(args, blob.Parameters);
        return InstallSession.ForTesting(blob, parsed);
    }

    [Fact]
    public void Auto_scope_defaults_to_user_and_never_requires_elevation()
    {
        var session = Session(InstallScope.Auto, "/silent");
        session.ResolvedScope.Should().Be(InstallScope.User);
        session.RequiresElevation.Should().BeFalse("a per-user install must stay prompt-free");
    }

    [Fact]
    public void AllUsers_flag_against_auto_resolves_machine()
    {
        var session = Session(InstallScope.Auto, "/silent", "/allusers");
        session.ResolvedScope.Should().Be(InstallScope.Machine);
    }

    [Fact]
    public void CurrentUser_flag_against_auto_resolves_user()
    {
        var session = Session(InstallScope.Auto, "/silent", "/currentuser");
        session.ResolvedScope.Should().Be(InstallScope.User);
    }

    [Fact]
    public void Machine_scope_requires_elevation_only_when_not_already_elevated()
    {
        var session = Session(InstallScope.Machine, "/silent");
        session.ResolvedScope.Should().Be(InstallScope.Machine);
        // The relaunch decision is exactly "machine scope AND not elevated".
        session.RequiresElevation.Should().Be(!Elevation.IsProcessElevated());
    }

    [Fact]
    public void AllUsers_against_fixed_user_manifest_is_exit_64()
    {
        // ScopeResolver throws UsageException, which the entry points map to 64.
        var act = () => Session(InstallScope.User, "/silent", "/allusers");
        act.Should().Throw<UsageException>();
    }

    [Fact]
    public void CurrentUser_against_fixed_machine_manifest_is_exit_64()
    {
        var act = () => Session(InstallScope.Machine, "/silent", "/currentuser");
        act.Should().Throw<UsageException>();
    }

    [Fact]
    public void Resolved_scope_is_usable_in_a_step_when_expression()
    {
        var blob = BlobWithScope(InstallScope.Auto);
        var parsed = CommandLineParser.Parse(new[] { "/silent" }, blob.Parameters);

        var machineCtx = StepContext.From(blob, parsed, null, null, InstallScope.Machine);
        machineCtx.Evaluate("scope == \"machine\"").Should().BeTrue();
        machineCtx.Evaluate("scope == \"user\"").Should().BeFalse();

        var userCtx = StepContext.From(blob, parsed, null, null, InstallScope.User);
        userCtx.Evaluate("scope == \"user\"").Should().BeTrue();
    }

    [Fact]
    public void Scope_root_resolves_to_the_per_scope_install_root()
    {
        var blob = BlobWithScope(InstallScope.Auto);
        var parsed = CommandLineParser.Parse(new[] { "/silent" }, blob.Parameters);

        var ctx = StepContext.From(blob, parsed, null, null, InstallScope.Machine);
        ctx.Resolve("${scope.root}")
            .Should().Be(ScopeLayout.For(InstallScope.Machine).InstallRoot);
    }

    /// <summary>
    /// Was <c>State_records_scope_and_is_honored_regardless_of_flag</c>, which asserted
    /// that a machine-scope load falls through to the user-scope directory and adopts
    /// the scope recorded inside the file. That is register row R1 clause (b) — the
    /// vulnerability, not a feature — so the test now asserts the refusal instead: a
    /// machine-scope load never reads <c>%LocalAppData%</c>, and the scope of a state
    /// file is the scope of the directory it was found in.
    /// </summary>
    [Fact]
    public void State_is_loaded_only_from_the_requested_scopes_own_directory()
    {
        var appId = "sigil.scope." + Guid.NewGuid().ToString("N");
        try
        {
            var journal = new RollbackJournal();

            // Installed per-user; the state file lands in %LocalAppData%.
            UninstallStateStore.Save(appId, journal, InstallScope.User);

            // An uninstall invoked preferring machine scope must not see it at all.
            UninstallStateStore.TryLoad(appId, InstallScope.Machine).Should().BeNull(
                "a machine-scope operation reading the user's own profile is R1");

            // The same state IS found in its own scope, and reports that scope.
            var loaded = UninstallStateStore.TryLoad(appId, InstallScope.User);
            loaded.Should().NotBeNull();
            loaded!.Scope.Should().Be(InstallScope.User);
        }
        finally
        {
#pragma warning disable CA1031
            try { UninstallStateStore.Delete(appId, InstallScope.User); } catch { /* best-effort */ }
#pragma warning restore CA1031
        }
    }
}
