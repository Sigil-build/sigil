using System.Linq;
using FluentAssertions;
using SigilBuild.Core.Configuration;
using SigilBuild.Core.Diagnostics;
using SigilBuild.Core.Manifest;
using Xunit;

namespace SigilBuild.Core.Tests.Manifest;

/// <summary>
/// R16: <c>allow_outside_install_dir</c> is the documented per-step opt-out from
/// destination containment. It is an envelope field — parsed once alongside
/// <c>when</c> / <c>on_failure</c> — but only the step types that actually write
/// somewhere accept the key.
/// </summary>
public class AllowOutsideInstallDirParseTests
{
    private static string Yaml(params string[] steps) =>
        "spec: v1.0\n" +
        "app: { id: com.example.App, name: App, version: 0.1.0, publisher: P }\n" +
        "build: { source: ./out }\n" +
        "install_steps:\n" +
        string.Concat(steps.Select(s => "  - " + s + "\n"));

    [Fact]
    public void Defaults_to_false_when_absent()
    {
        var result = ManifestParser.Parse(
            Yaml("{ id: cp, type: file_copy, from: a, to: b }"), "s.yaml");

        result.Diagnostics.Should().NotContain(d => d.Severity == DiagnosticSeverity.Error);
        result.Manifest!.InstallSteps!.Single().AllowOutsideInstallDir.Should().BeFalse();
    }

    [Theory]
    [InlineData("{ id: s, type: file_copy, from: a, to: b, allow_outside_install_dir: true }")]
    [InlineData("{ id: s, type: file_delete, path: a, allow_outside_install_dir: true }")]
    [InlineData("{ id: s, type: directory_delete, path: a, allow_outside_install_dir: true }")]
    [InlineData("{ id: s, type: http_download, url: 'https://e.com/f', dest: d, sha256: 'aa', allow_outside_install_dir: true }")]
    [InlineData("{ id: s, type: ini_write, path: a.ini, section: app, key: k, value: v, allow_outside_install_dir: true }")]
    [InlineData("{ id: s, type: json_edit, path: a.json, pointer: /a, value: '1', allow_outside_install_dir: true }")]
    [InlineData("{ id: s, type: xml_edit, path: a.xml, xpath: /root/a, value: v, allow_outside_install_dir: true }")]
    public void Every_containing_step_type_accepts_it_without_a_diagnostic(string step)
    {
        var result = ManifestParser.Parse(Yaml(step), "s.yaml");

        result.Diagnostics.Should().NotContain(d => d.Severity == DiagnosticSeverity.Error);
        result.Diagnostics.Should().NotContain(
            d => d.Code == DiagnosticCodes.StepParameterMismatch,
            "the key is part of these step types' envelope, not an unrecognized field");
        result.Manifest!.InstallSteps!.Single().AllowOutsideInstallDir.Should().BeTrue();
    }

    [Theory]
    [InlineData("{ id: s, type: registry_write, hive: HKCU, key: k, name: n, value: v, allow_outside_install_dir: true }")]
    [InlineData("{ id: s, type: directory_create, path: a, allow_outside_install_dir: true }")]
    public void A_step_type_with_no_contained_destination_reports_it_as_unrecognized(string step)
    {
        // Silently ignoring it would let a manifest believe it had relaxed
        // something it had not.
        var result = ManifestParser.Parse(Yaml(step), "s.yaml");

        result.Diagnostics.Should().Contain(d => d.Code == DiagnosticCodes.StepParameterMismatch);
    }

    [Fact]
    public void It_is_not_an_opt_out_for_the_privileged_step_targets()
    {
        // scheduled_task_create / service_install / com_register / firewall_rule
        // run with SYSTEM-level authority (R3/R9) and have no opt-out at all, so
        // the key is not in their envelope either.
        var result = ManifestParser.Parse(
            Yaml("{ id: s, type: scheduled_task_create, name: T, program: p, trigger: logon, " +
                 "allow_outside_install_dir: true }"),
            "s.yaml");

        result.Diagnostics.Should().Contain(d => d.Code == DiagnosticCodes.StepParameterMismatch);
    }
}
