using System;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using Xunit;

namespace SigilBuild.Core.Tests.Manifest;

/// <summary>
/// P1: dependency ordering + cycle detection for <c>installer.vars</c>
/// (<see cref="InstallerVarGraph"/>). Purely structural — no expression engine.
/// </summary>
public class InstallerVarGraphTests
{
    private static InstallerVar V(string name, string expr) => new(name, expr);

    [Fact]
    public void TopologicalOrder_places_dependencies_before_dependents()
    {
        var vars = new[]
        {
            V("c", "var.b"),
            V("a", "'x'"),
            V("b", "var.a"),
        };

        InstallerVarGraph.TopologicalOrder(vars).Should().Equal("a", "b", "c");
    }

    [Fact]
    public void TopologicalOrder_ignores_non_var_identifiers_and_functions()
    {
        var vars = new[]
        {
            V("a", "registry_read('HKLM', 'k', 'v')"),
            V("b", "param.edition == 'pro'"),
        };

        // No cross-var deps → declaration order is a valid order.
        InstallerVarGraph.TopologicalOrder(vars).Should().BeEquivalentTo(new[] { "a", "b" });
    }

    [Fact]
    public void TopologicalOrder_detects_a_two_var_cycle()
    {
        var vars = new[] { V("a", "var.b"), V("b", "var.a") };

        var act = () => InstallerVarGraph.TopologicalOrder(vars);

        act.Should().Throw<InstallerVarCycleException>()
            .Which.Cycle.Should().Contain("a").And.Contain("b");
    }

    [Fact]
    public void TopologicalOrder_detects_a_self_cycle()
    {
        var vars = new[] { V("a", "var.a") };

        var act = () => InstallerVarGraph.TopologicalOrder(vars);

        act.Should().Throw<InstallerVarCycleException>();
    }

    [Fact]
    public void ReferencedIdentifiers_skips_string_literals_and_call_names()
    {
        var ids = InstallerVarGraph.ReferencedIdentifiers(
            "registry_read('HKLM', var.key, 'var.not_a_ref')");

        ids.Should().Contain("var.key");
        ids.Should().NotContain("registry_read", "a name followed by '(' is a call, not a reference");
        ids.Should().NotContain("var.not_a_ref", "text inside a string literal is not a reference");
        ids.Should().NotContain("HKLM");
    }
}
