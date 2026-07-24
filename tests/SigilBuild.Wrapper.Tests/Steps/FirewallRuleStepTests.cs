namespace SigilBuild.Wrapper.Tests.Steps;

using System;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Steps;
using Xunit;

/// <summary>
/// T11.3 (P11): the <c>firewall_rule</c> step. The exact
/// <c>netsh advfirewall firewall add rule</c> / <c>delete rule</c> argument
/// construction is proven via the pure
/// <see cref="FirewallRuleStep.BuildAddArgs"/> / <see cref="FirewallRuleStep.BuildDeleteArgs"/>
/// seams, which need neither Windows nor admin rights. The live
/// add → show rule → reverse leg is CI-VM-only (needs an elevated netsh
/// call); see
/// <see cref="Journal_records_DeleteFirewallRule_before_attempting_the_add"/>
/// for the one locally-runnable end-to-end assertion (journal ordering, not
/// success — this sandbox is not elevated).
/// </summary>
[SupportedOSPlatform("windows")]
public class FirewallRuleStepTests
{
    [Fact]
    public void BuildAddArgs_minimal_rule_with_only_required_fields()
    {
        var args = FirewallRuleStep.BuildAddArgs(
            "AcmeApp", "in", "allow", program: null, port: null, protocol: null);

        args.Should().Equal(
            "advfirewall", "firewall", "add", "rule",
            "name=AcmeApp",
            "dir=in",
            "action=allow",
            "enable=yes");
    }

    [Fact]
    public void BuildAddArgs_appends_program_localport_and_protocol_when_present()
    {
        var args = FirewallRuleStep.BuildAddArgs(
            "AcmeApp", "in", "allow",
            program: @"C:\Program Files\Acme\AcmeApp.exe", port: 8443, protocol: "tcp");

        args.Should().Equal(
            "advfirewall", "firewall", "add", "rule",
            "name=AcmeApp",
            "dir=in",
            "action=allow",
            @"program=C:\Program Files\Acme\AcmeApp.exe",
            "localport=8443",
            "protocol=tcp",
            "enable=yes");
    }

    [Fact]
    public void BuildAddArgs_out_direction_and_block_action()
    {
        var args = FirewallRuleStep.BuildAddArgs(
            "BlockOutbound", "out", "block", program: null, port: null, protocol: null);

        args.Should().Equal(
            "advfirewall", "firewall", "add", "rule",
            "name=BlockOutbound",
            "dir=out",
            "action=block",
            "enable=yes");
    }

    [Fact]
    public void BuildAddArgs_each_kv_pair_is_a_single_token_with_no_space_around_the_equals()
    {
        // The brief's ArgumentList caveat: netsh expects name=Foo as ONE token,
        // not "name=" "Foo" or "name" "=Foo".
        var args = FirewallRuleStep.BuildAddArgs(
            "AcmeApp", "in", "allow", program: null, port: 80, protocol: "udp");

        args.Should().Contain("name=AcmeApp");
        args.Should().Contain("dir=in");
        args.Should().Contain("action=allow");
        args.Should().Contain("localport=80");
        args.Should().Contain("protocol=udp");
        args.Should().NotContain("name=");
        args.Should().NotContain("=");
    }

    [Fact]
    public void BuildDeleteArgs_targets_the_rule_by_name_only()
    {
        var args = FirewallRuleStep.BuildDeleteArgs("AcmeApp");

        args.Should().Equal("advfirewall", "firewall", "delete", "rule", "name=AcmeApp");
    }

    [Fact]
    public void BuildAddArgs_is_repeatable_and_deterministic()
    {
        var a = FirewallRuleStep.BuildAddArgs("AcmeApp", "in", "allow", "app.exe", 443, "tcp");
        var b = FirewallRuleStep.BuildAddArgs("AcmeApp", "in", "allow", "app.exe", 443, "tcp");

        a.Should().Equal(b);
    }

    [Fact]
    public async Task Journal_records_DeleteFirewallRule_before_attempting_the_add()
    {
        // Locally-runnable end-to-end assertion (no admin required): the
        // rollback record is appended BEFORE netsh.exe runs, so even if the
        // add itself fails (e.g. access denied — this sandbox is not
        // elevated) or the process never returns, the journal already knows
        // how to undo. The live add -> show rule -> reverse leg needs an
        // elevated netsh call and is verified on the CI VM (AGENTS.md §2).
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var spec = new InstallStep.FirewallRule(
            "fw", "SigilTestRule_DoesNotPersist", "in", "allow",
            Program: null, Port: null, Protocol: null, When: null, OnFailure: OnFailure.Continue);
        var journal = new RollbackJournal();

        await new FirewallRuleStep(spec).RunAsync(StepContext.Empty, journal, default);

        journal.Records.Should().ContainSingle()
            .Which.Should().BeOfType<RollbackRecord.DeleteFirewallRule>()
            .Which.RuleName.Should().Be("SigilTestRule_DoesNotPersist");
    }
}
