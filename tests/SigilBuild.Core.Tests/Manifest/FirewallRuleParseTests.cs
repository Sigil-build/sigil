using System.Linq;
using FluentAssertions;
using SigilBuild.Core.Configuration;
using SigilBuild.Core.Diagnostics;
using SigilBuild.Core.Manifest;
using Xunit;

namespace SigilBuild.Core.Tests.Manifest;

/// <summary>
/// T11.3 (P11): parsing of the <c>firewall_rule</c> install step — third and
/// last of three machine-scope-only "system steps". Covers the happy path,
/// SIG0232 (missing required <c>name</c>/<c>direction</c>/<c>action</c>),
/// SIG0233 (bad enum value) for <c>direction</c>/<c>action</c>/<c>protocol</c>,
/// the port/protocol default rule (protocol defaults to <c>tcp</c> when
/// <c>port</c> is given and <c>protocol</c> is absent), and the positive
/// SIG0310 (<see cref="DiagnosticCodes.SystemStepRequiresMachineScope"/>) case:
/// this step overrides <see cref="InstallStep.RequiresMachineScope"/> to
/// <c>true</c>, so a manifest not pinned to <c>scope: machine</c> is refused at
/// pack time.
/// </summary>
public class FirewallRuleParseTests
{
    private static string Yaml(string step, string scopeLine = "scope: machine") =>
        "spec: v1.0\n" +
        "app: { id: com.example.App, name: App, version: 0.1.0, publisher: P }\n" +
        "build: { source: ./out }\n" +
        "installer:\n  " + scopeLine + "\n" +
        "install_steps:\n  - " + step + "\n";

    private const string HappyStep =
        "{ id: fw, type: firewall_rule, name: AcmeApp, direction: in, action: allow, " +
        "program: \"{install_dir}/AcmeApp.exe\", port: 8443, protocol: tcp }";

    [Fact]
    public void Parses_happy_path()
    {
        var result = ManifestParser.Parse(Yaml(HappyStep), "s.yaml");
        result.Diagnostics.Should().NotContain(d => d.Severity == DiagnosticSeverity.Error);

        var step = result.Manifest!.InstallSteps!.OfType<InstallStep.FirewallRule>().Single();
        step.Name.Should().Be("AcmeApp");
        step.Direction.Should().Be("in");
        step.Action.Should().Be("allow");
        step.Program.Should().Be("{install_dir}/AcmeApp.exe");
        step.Port.Should().Be(8443);
        step.Protocol.Should().Be("tcp");
        step.RequiresMachineScope.Should().BeTrue();
    }

    [Fact]
    public void Parses_minimal_step_with_only_required_fields()
    {
        var step = "{ id: fw, type: firewall_rule, name: AcmeApp, direction: out, action: block }";

        var result = ManifestParser.Parse(Yaml(step), "s.yaml");
        result.Diagnostics.Should().NotContain(d => d.Severity == DiagnosticSeverity.Error);

        var parsed = result.Manifest!.InstallSteps!.OfType<InstallStep.FirewallRule>().Single();
        parsed.Program.Should().BeNull();
        parsed.Port.Should().BeNull();
        parsed.Protocol.Should().BeNull("protocol is only defaulted when port is given");
    }

    [Fact]
    public void Protocol_defaults_to_tcp_when_port_is_set_and_protocol_is_absent()
    {
        var step = "{ id: fw, type: firewall_rule, name: AcmeApp, direction: in, action: allow, port: 443 }";

        var result = ManifestParser.Parse(Yaml(step), "s.yaml");
        result.Diagnostics.Should().NotContain(d => d.Severity == DiagnosticSeverity.Error);

        var parsed = result.Manifest!.InstallSteps!.OfType<InstallStep.FirewallRule>().Single();
        parsed.Port.Should().Be(443);
        parsed.Protocol.Should().Be("tcp");
    }

    [Theory]
    [InlineData("{ id: fw, type: firewall_rule, direction: in, action: allow }")]              // no name
    [InlineData("{ id: fw, type: firewall_rule, name: AcmeApp, action: allow }")]               // no direction
    [InlineData("{ id: fw, type: firewall_rule, name: AcmeApp, direction: in }")]               // no action
    public void Missing_required_field_emits_SIG0232(string step)
    {
        var result = ManifestParser.Parse(Yaml(step), "s.yaml");
        result.Diagnostics.Should().Contain(d => d.Code == DiagnosticCodes.MissingRequiredStepField);
        result.Manifest!.InstallSteps!.OfType<InstallStep.FirewallRule>().Should().BeEmpty();
    }

    [Fact]
    public void Unknown_direction_value_emits_SIG0233()
    {
        var step = "{ id: fw, type: firewall_rule, name: AcmeApp, direction: sideways, action: allow }";

        var result = ManifestParser.Parse(Yaml(step), "s.yaml");
        var d = result.Diagnostics.Should().ContainSingle(d => d.Code == DiagnosticCodes.InvalidStepFieldValue).Which;
        d.Severity.Should().Be(DiagnosticSeverity.Error);
        d.Message.Should().Contain("direction");
        d.Message.Should().Contain("sideways");
        result.Manifest!.InstallSteps!.OfType<InstallStep.FirewallRule>().Should().BeEmpty();
    }

    [Fact]
    public void Unknown_action_value_emits_SIG0233()
    {
        var step = "{ id: fw, type: firewall_rule, name: AcmeApp, direction: in, action: permit }";

        var result = ManifestParser.Parse(Yaml(step), "s.yaml");
        var d = result.Diagnostics.Should().ContainSingle(d => d.Code == DiagnosticCodes.InvalidStepFieldValue).Which;
        d.Severity.Should().Be(DiagnosticSeverity.Error);
        d.Message.Should().Contain("action");
        d.Message.Should().Contain("permit");
        result.Manifest!.InstallSteps!.OfType<InstallStep.FirewallRule>().Should().BeEmpty();
    }

    [Fact]
    public void Unknown_protocol_value_emits_SIG0233()
    {
        var step =
            "{ id: fw, type: firewall_rule, name: AcmeApp, direction: in, action: allow, " +
            "port: 80, protocol: sctp }";

        var result = ManifestParser.Parse(Yaml(step), "s.yaml");
        var d = result.Diagnostics.Should().ContainSingle(d => d.Code == DiagnosticCodes.InvalidStepFieldValue).Which;
        d.Severity.Should().Be(DiagnosticSeverity.Error);
        d.Message.Should().Contain("protocol");
        d.Message.Should().Contain("sctp");
        result.Manifest!.InstallSteps!.OfType<InstallStep.FirewallRule>().Should().BeEmpty();
    }

    // ---- SIG0310: firewall_rule is machine-scope-only. scope: user / scope:
    // auto must trip it pointing at this step's own node; scope: machine must
    // not. ----

    [Theory]
    [InlineData("scope: user")]
    [InlineData("scope: auto")]
    public void User_or_auto_scope_trips_SIG0310_pointing_at_the_step(string scopeLine)
    {
        var result = ManifestParser.Parse(Yaml(HappyStep, scopeLine), "s.yaml");

        var d = result.Diagnostics.Should()
            .ContainSingle(d => d.Code == DiagnosticCodes.SystemStepRequiresMachineScope).Which;
        d.Severity.Should().Be(DiagnosticSeverity.Error);
        d.Message.Should().Contain("'fw'");
        // Points at the step's own node, never line 1 (the manifest root).
        d.Location.Line.Should().BeGreaterThan(1);
    }

    [Fact]
    public void Machine_scope_does_not_trip_SIG0310()
    {
        var result = ManifestParser.Parse(Yaml(HappyStep, "scope: machine"), "s.yaml");

        result.Diagnostics.Should().NotContain(d => d.Code == DiagnosticCodes.SystemStepRequiresMachineScope);
    }
}
