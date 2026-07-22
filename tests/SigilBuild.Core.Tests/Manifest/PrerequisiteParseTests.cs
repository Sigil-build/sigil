using FluentAssertions;
using SigilBuild.Core.Configuration;
using SigilBuild.Core.Diagnostics;
using SigilBuild.Core.Manifest;
using Xunit;

namespace SigilBuild.Core.Tests.Manifest;

/// <summary>
/// P5 (gap G6): pack-time parsing + validation of <c>installer.prerequisites[]</c>.
/// The headline rule is that an <c>https://</c> source without a <c>sha256</c> is
/// refused (SIG0280) — a download without an integrity check never ships.
/// </summary>
public class PrerequisiteParseTests
{
    private static string Yaml(string prereqBody) =>
        "spec: v1.0\n" +
        "app: { id: com.example.App, name: App, version: 0.1.0, publisher: P }\n" +
        "build: { source: ./out }\n" +
        "installer:\n" +
        "  prerequisites:\n" +
        "    - " + prereqBody + "\n";

    private static InstallerPrerequisite? Single(string prereqBody, out System.Collections.Generic.IReadOnlyList<Diagnostic> diags)
    {
        var result = ManifestParser.Parse(Yaml(prereqBody), "s.yaml");
        diags = result.Diagnostics;
        var prereqs = result.Manifest?.Installer?.Prerequisites;
        return prereqs is { Count: > 0 } ? prereqs[0] : null;
    }

    [Fact]
    public void Valid_https_prerequisite_with_sha256_parses_all_fields()
    {
        var p = Single(
            "{ name: \"VC++ 2015-2022\", detect: \"registry_exists('HKLM', 'k', 'v')\", " +
            "source: \"https://ex.com/vc_redist.x64.exe\", sha256: \"abc123\", " +
            "args: [\"/install\", \"/quiet\", \"/norestart\"], exit_codes_ok: [0, 3010], " +
            "scope_required: allusers, timeout_seconds: 120 }",
            out var diags);

        diags.Should().NotContain(d => d.Severity == DiagnosticSeverity.Error);
        p.Should().NotBeNull();
        p!.Name.Should().Be("VC++ 2015-2022");
        p.Source.Should().Be("https://ex.com/vc_redist.x64.exe");
        p.Sha256.Should().Be("abc123");
        p.Args.Should().Equal("/install", "/quiet", "/norestart");
        p.ExitCodesOk.Should().Equal(0, 3010);
        p.ScopeRequired.Should().Be("allusers");
        p.TimeoutSeconds.Should().Be(120);
    }

    [Fact]
    public void Payload_source_without_sha256_is_allowed()
    {
        var p = Single(
            "{ name: R, detect: \"file_exists('c:/x')\", source: \"payload://prereq/vc.exe\" }",
            out var diags);

        diags.Should().NotContain(d => d.Severity == DiagnosticSeverity.Error);
        p.Should().NotBeNull();
        p!.Sha256.Should().BeNull();
    }

    [Fact]
    public void Https_source_without_sha256_is_a_fatal_SIG0280()
    {
        Single("{ name: R, detect: \"file_exists('c:/x')\", source: \"https://ex.com/vc.exe\" }", out var diags);

        diags.Should().Contain(d =>
            d.Code == DiagnosticCodes.InvalidPrerequisite && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Http_source_is_a_fatal_SIG0280()
    {
        Single("{ name: R, detect: \"file_exists('c:/x')\", source: \"http://ex.com/vc.exe\", sha256: abc }", out var diags);
        diags.Should().Contain(d => d.Code == DiagnosticCodes.InvalidPrerequisite && d.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("{ detect: \"file_exists('c:/x')\", source: \"payload://a.exe\" }")]        // no name
    [InlineData("{ name: R, source: \"payload://a.exe\" }")]                                 // no detect
    [InlineData("{ name: R, detect: \"file_exists('c:/x')\" }")]                             // no source
    public void Missing_required_field_is_a_fatal_SIG0280(string body)
    {
        Single(body, out var diags);
        diags.Should().Contain(d => d.Code == DiagnosticCodes.InvalidPrerequisite && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Malformed_detect_expression_is_a_fatal_SIG0280()
    {
        // Unbalanced parenthesis — caught structurally at pack time.
        Single("{ name: R, detect: \"registry_exists('HKLM', 'k'\", source: \"payload://a.exe\" }", out var diags);
        diags.Should().Contain(d => d.Code == DiagnosticCodes.InvalidPrerequisite && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Invalid_scope_required_is_a_fatal_SIG0280()
    {
        Single(
            "{ name: R, detect: \"file_exists('c:/x')\", source: \"payload://a.exe\", scope_required: admin }",
            out var diags);
        diags.Should().Contain(d => d.Code == DiagnosticCodes.InvalidPrerequisite && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Exit_codes_ok_defaults_to_null_when_omitted()
    {
        var p = Single("{ name: R, detect: \"file_exists('c:/x')\", source: \"payload://a.exe\" }", out _);
        p!.ExitCodesOk.Should().BeNull("the runner applies the [0] default at run time");
    }
}
