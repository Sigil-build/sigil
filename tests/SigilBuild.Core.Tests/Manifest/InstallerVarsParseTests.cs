using System.Linq;
using System.Text;
using FluentAssertions;
using SigilBuild.Core.Configuration;
using SigilBuild.Core.Diagnostics;
using Xunit;

namespace SigilBuild.Core.Tests.Manifest;

/// <summary>
/// P1: parsing of the <c>installer.vars</c> block into
/// <see cref="SigilBuild.Core.Manifest.InstallerSection.Vars"/>, including the
/// pack-time cycle / malformed-expression diagnostics (SIG0270).
/// </summary>
public class InstallerVarsParseTests
{
    private static string Yaml(params string[] varLines)
    {
        var sb = new StringBuilder();
        sb.Append("spec: v1.0\n");
        sb.Append("app: { id: com.example.App, name: App, version: 0.1.0, publisher: P }\n");
        sb.Append("build: { source: ./out }\n");
        sb.Append("installer:\n");
        sb.Append("  vars:\n");
        foreach (var line in varLines)
        {
            sb.Append("    ").Append(line).Append('\n');
        }
        return sb.ToString();
    }

    [Fact]
    public void Parses_vars_in_declaration_order()
    {
        var result = ManifestParser.Parse(
            Yaml(
                "old_path: \"registry_read('HKLM', 'k', 'Path')\"",
                "is_upgrade: \"installed_version(app.id) != ''\""),
            "s.yaml");

        result.Diagnostics.Should().NotContain(d => d.Severity == DiagnosticSeverity.Error);
        var vars = result.Manifest!.Installer!.Vars!;
        vars.Should().HaveCount(2);
        vars[0].Name.Should().Be("old_path");
        vars[1].Name.Should().Be("is_upgrade");
        vars[1].Expression.Should().Contain("installed_version");
    }

    [Fact]
    public void Backslash_paths_in_var_expressions_parse_without_error()
    {
        // A registry key path carries backslashes inside a string literal — the
        // var validator must be quote-aware and not reject them.
        var result = ManifestParser.Parse(
            Yaml("reg: \"registry_read('HKLM', 'Software\\\\Acme\\\\App', 'InstallPath')\""),
            "s.yaml");

        result.Diagnostics.Should().NotContain(d => d.Code == DiagnosticCodes.InvalidInstallerVar);
        result.Manifest!.Installer!.Vars!.Single().Expression
            .Should().Contain(@"Software\Acme\App");
    }

    [Fact]
    public void Cyclic_vars_emit_a_fatal_SIG0270()
    {
        var result = ManifestParser.Parse(Yaml("a: \"var.b\"", "b: \"var.a\""), "s.yaml");

        result.Diagnostics.Should().Contain(d =>
            d.Code == DiagnosticCodes.InvalidInstallerVar && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Malformed_var_expression_emits_SIG0270()
    {
        // Unbalanced parenthesis.
        var result = ManifestParser.Parse(Yaml("bad: \"registry_read('x'\""), "s.yaml");

        result.Diagnostics.Should().Contain(d => d.Code == DiagnosticCodes.InvalidInstallerVar);
    }
}
