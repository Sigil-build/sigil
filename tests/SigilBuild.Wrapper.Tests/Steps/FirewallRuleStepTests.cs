namespace SigilBuild.Wrapper.Tests.Steps;

using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Steps;
using SigilBuild.Wrapper.Tests.Helpers;
using Xunit;

/// <summary>
/// T11.3 (P11): the <c>firewall_rule</c> step. The exact
/// <c>netsh advfirewall firewall add rule</c> / <c>delete rule</c> argument
/// construction is proven via the pure
/// <see cref="FirewallRuleStep.BuildAddArgs"/> / <see cref="FirewallRuleStep.BuildDeleteArgs"/>
/// seams, which need neither Windows nor admin rights. The live
/// add → show rule → reverse leg is CI-VM-only (needs an elevated netsh
/// call); see
/// <see cref="Journal_records_DeleteFirewallRule_before_netsh_can_mutate_anything"/>
/// for the one locally-runnable end-to-end assertion (journal ordering, proven
/// by cancellation rather than by the host happening to be unprivileged — no
/// test in this class may create a firewall rule on any host, elevated or not).
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

    /// <summary>
    /// The rollback record is appended BEFORE netsh.exe mutates anything, so a
    /// crash or a cancellation part-way through still leaves the journal able to
    /// undo. Locally runnable on any Windows host, elevated or not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This test must never create a firewall rule, on any host.</b> The
    /// previous version relied on the sandbox being unelevated for the
    /// <c>add rule</c> to fail — but CI runs elevated, so there it really did add
    /// <c>SigilTestRule_DoesNotPersist</c> to the runner's firewall and never
    /// removed it (nothing invoked the journal's undo). It is now driven with an
    /// already-cancelled <see cref="CancellationToken"/>: <c>RunAsync</c> appends
    /// the journal record, then <c>await</c>s the idempotency pre-delete, which
    /// throws <see cref="OperationCanceledException"/> immediately — so the
    /// <c>add rule</c> line is unreachable by construction, not by luck of the
    /// host's privilege level. The single netsh invocation that does start is a
    /// <c>delete rule</c> for a per-run GUID name that cannot exist, i.e. a no-op
    /// whether or not the host is elevated.
    /// </para>
    /// <para>
    /// The live add → show rule → reverse leg needs an elevated netsh call, a
    /// uniquely-named rule and a <c>finally</c> cleanup; it lives in
    /// <c>SigilBuild.Wrapper.IntegrationTests.FirewallRuleInstallTests</c> behind
    /// <c>[VmSystemStepsFact]</c> (AGENTS.md §2).
    /// </para>
    /// </remarks>
    [WindowsFact("netsh.exe is Windows-only")]
    public async Task Journal_records_DeleteFirewallRule_before_netsh_can_mutate_anything()
    {
        var ruleName = "SigilUnitRule_" + Guid.NewGuid().ToString("N");
        var spec = new InstallStep.FirewallRule(
            "fw", ruleName, "in", "allow",
            Program: null, Port: null, Protocol: null, When: null, OnFailure: OnFailure.Continue);
        var journal = new RollbackJournal();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await new FirewallRuleStep(spec)
            .RunAsync(StepContext.Empty, journal, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "cancellation must abort the step before the add rule line is reached");
        journal.Records.Should().ContainSingle()
            .Which.Should().BeOfType<RollbackRecord.DeleteFirewallRule>()
            .Which.RuleName.Should().Be(ruleName);
    }
}
