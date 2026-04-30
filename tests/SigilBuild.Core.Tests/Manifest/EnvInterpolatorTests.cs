using System.Collections.Generic;
using FluentAssertions;
using SigilBuild.Core.Configuration;
using SigilBuild.Core.Diagnostics;
using Xunit;

namespace SigilBuild.Core.Tests.Manifest;

public class EnvInterpolatorTests
{
    private sealed class MapEnv : IEnvironmentReader
    {
        private readonly Dictionary<string, string> _data;
        public MapEnv(Dictionary<string, string> data) { _data = data; }
        public string? Get(string name) => _data.TryGetValue(name, out var v) ? v : null;
    }

    [Fact]
    public void Expand_SubstitutesKnownVariable()
    {
        var env = new MapEnv(new() { ["MY_VAR"] = "hello" });

        var result = EnvInterpolator.Expand("path: ${MY_VAR}/file", env);

        result.Output.Should().Be("path: hello/file");
        result.Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Expand_UnknownVariable_ReportsDiagnosticAndLeavesPlaceholder()
    {
        var env = new MapEnv(new());

        var result = EnvInterpolator.Expand("token: ${MISSING}", env);

        result.Diagnostics.Should().ContainSingle()
            .Which.Code.Should().Be(DiagnosticCodes.EnvVariableMissing);
        result.Output.Should().Be("token: ${MISSING}");
    }

    [Theory]
    [InlineData("$$VAR", "$$VAR")]
    [InlineData("escaped: $${LITERAL}", "escaped: ${LITERAL}")]
    public void Expand_DoubleDollarEscapes(string input, string expected)
    {
        var env = new MapEnv(new());
        var result = EnvInterpolator.Expand(input, env);
        result.Output.Should().Be(expected);
        result.Diagnostics.Should().BeEmpty();
    }
}
