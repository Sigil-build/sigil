using System.Collections.Generic;
using FluentAssertions;
using SigilBuild.Wrapper.Expressions;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Expressions;

public class EvaluatorTests
{
    private static readonly IReadOnlyDictionary<string, object?> Ctx = new Dictionary<string, object?>
    {
        ["parameters.edition"] = "pro",
        ["parameters.count"] = 3L,
        ["system.os"] = "10.0.22631",
        ["system.arch"] = "x64",
        ["env.PATH"] = "C:\\Windows",
    };

    [Theory]
    [InlineData("parameters.edition == 'pro'", true)]
    [InlineData("parameters.edition == 'community'", false)]
    [InlineData("parameters.edition != 'community'", true)]
    [InlineData("parameters.count > 2", true)]
    [InlineData("parameters.count >= 3 && parameters.count <= 5", true)]
    [InlineData("!(parameters.edition == 'pro')", false)]
    [InlineData("parameters.edition in ['pro', 'enterprise']", true)]
    [InlineData("parameters.edition not_in ['community', 'student']", true)]
    [InlineData("defined(parameters.edition)", true)]
    [InlineData("defined(parameters.bogus)", false)]
    [InlineData("empty(parameters.edition)", false)]
    [InlineData("version_gte(system.os, '10.0.19041')", true)]
    [InlineData("version_gte(system.os, '11.0.0')", false)]
    [InlineData("os_version() != ''", true)]
    [InlineData("arch() != ''", true)]
    public void Evaluator_handles_full_operator_and_function_set(string expr, bool expected)
        => new Evaluator().EvaluateBool(expr, Ctx).Should().Be(expected);

    [Theory]
    [InlineData("nuke_disk()")]
    [InlineData("System.IO.File.Delete('a')")]
    public void Forbidden_constructs_throw(string expr)
    {
        var act = () => new Evaluator().EvaluateBool(expr, Ctx);
        act.Should().Throw<ExpressionException>();
    }

    [Fact]
    public void Mismatched_types_in_comparison_throw()
    {
        var act = () => new Evaluator().EvaluateBool("parameters.edition < 5", Ctx);
        act.Should().Throw<ExpressionException>();
    }

    [Fact]
    public void Empty_handles_string_list_and_null()
    {
        var ctx = new Dictionary<string, object?>
        {
            ["x.string"] = string.Empty,
            ["x.list"] = System.Array.Empty<object?>(),
            ["x.null"] = null,
        };
        var ev = new Evaluator();
        ev.EvaluateBool("empty(x.string)", ctx).Should().BeTrue();
        ev.EvaluateBool("empty(x.list)", ctx).Should().BeTrue();
        ev.EvaluateBool("empty(x.null)", ctx).Should().BeTrue();
    }
}
