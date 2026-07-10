using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Cli;
using SigilBuild.Wrapper.Engine;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Engine;

/// <summary>
/// T12 scope-resolution decision table: the manifest scope × <c>/allusers</c> /
/// <c>/currentuser</c> flag matrix, including the exit-64 (UsageException)
/// conflicts against a fixed manifest scope.
/// </summary>
public sealed class ScopeResolverTests
{
    [Theory]
    // auto: defaults to user; freely overridable by flag.
    [InlineData(InstallScope.Auto, ScopeOverride.None, InstallScope.User)]
    [InlineData(InstallScope.Auto, ScopeOverride.CurrentUser, InstallScope.User)]
    [InlineData(InstallScope.Auto, ScopeOverride.AllUsers, InstallScope.Machine)]
    // fixed user: /currentuser agrees (no-op), no flag stays user.
    [InlineData(InstallScope.User, ScopeOverride.None, InstallScope.User)]
    [InlineData(InstallScope.User, ScopeOverride.CurrentUser, InstallScope.User)]
    // fixed machine: /allusers agrees (no-op), no flag stays machine.
    [InlineData(InstallScope.Machine, ScopeOverride.None, InstallScope.Machine)]
    [InlineData(InstallScope.Machine, ScopeOverride.AllUsers, InstallScope.Machine)]
    public void Resolve_maps_manifest_scope_and_flag_to_effective_scope(
        InstallScope manifest, ScopeOverride flag, InstallScope expected)
    {
        ScopeResolver.Resolve(manifest, flag).Should().Be(expected);
    }

    [Fact]
    public void AllUsers_against_fixed_user_is_a_usage_error()
    {
        var act = () => ScopeResolver.Resolve(InstallScope.User, ScopeOverride.AllUsers);
        act.Should().Throw<UsageException>().WithMessage("*user*");
    }

    [Fact]
    public void CurrentUser_against_fixed_machine_is_a_usage_error()
    {
        var act = () => ScopeResolver.Resolve(InstallScope.Machine, ScopeOverride.CurrentUser);
        act.Should().Throw<UsageException>().WithMessage("*machine*");
    }

    [Fact]
    public void Resolve_never_returns_auto()
    {
        // Whatever the inputs, the effective scope is always a concrete target.
        ScopeResolver.Resolve(InstallScope.Auto, ScopeOverride.None)
            .Should().BeOneOf(InstallScope.User, InstallScope.Machine);
    }
}
