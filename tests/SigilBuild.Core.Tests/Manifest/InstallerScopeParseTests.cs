using FluentAssertions;
using SigilBuild.Core.Configuration;
using SigilBuild.Core.Diagnostics;
using SigilBuild.Core.Manifest;
using Xunit;

namespace SigilBuild.Core.Tests.Manifest;

/// <summary>
/// T12: parsing of <c>installer.scope</c> (user | machine | auto) into
/// <see cref="InstallerSection.Scope"/>.
/// </summary>
public class InstallerScopeParseTests
{
    private static string Yaml(string scopeLine) => $$"""
        spec: v1.0
        app: { id: com.example.App, name: App, version: 0.1.0, publisher: P }
        build: { source: ./out }
        installer:
          {{scopeLine}}
          brand:
            primaryColor: "#312E81"
            accentColor: "#4F46E5"
        """;

    [Theory]
    [InlineData("scope: user", InstallScope.User)]
    [InlineData("scope: machine", InstallScope.Machine)]
    [InlineData("scope: auto", InstallScope.Auto)]
    [InlineData("scope: MACHINE", InstallScope.Machine)]
    public void Parses_scope_enum(string scopeLine, InstallScope expected)
    {
        var result = ManifestParser.Parse(Yaml(scopeLine), "s.yaml");
        result.Diagnostics.Should().BeEmpty();
        result.Manifest!.Installer!.Scope.Should().Be(expected);
    }

    [Fact]
    public void Absent_scope_defaults_to_auto()
    {
        var result = ManifestParser.Parse(Yaml("# no scope"), "s.yaml");
        result.Manifest!.Installer!.Scope.Should().Be(InstallScope.Auto);
    }

    [Fact]
    public void Unknown_scope_falls_back_to_auto_with_a_diagnostic()
    {
        // Bypass the schema enum gate by parsing directly; the parser must be
        // lenient and warn rather than throw.
        var result = ManifestParser.Parse(Yaml("scope: everyone"), "s.yaml");
        result.Manifest!.Installer!.Scope.Should().Be(InstallScope.Auto);
        result.Diagnostics.Should().Contain(d => d.Code == DiagnosticCodes.InvalidInstallerScope);
    }
}
