namespace SigilBuild.Wrapper.IntegrationTests;

using System;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Steps;
using Xunit;

/// <summary>
/// T13.1 (P13): the live create + reverse leg for <c>firewall_rule</c> (P11 /
/// T11.3), deferred to CI-VM when that step shipped with unit/parse/roundtrip
/// coverage only. Drives <see cref="FirewallRuleStep"/> directly (the
/// <c>netsh</c> argument construction itself is already proven byte-for-byte
/// by <c>FirewallRuleStepTests.BuildAddArgs_*</c>/<c>BuildDeleteArgs_*</c>;
/// this test is only about the real <c>netsh advfirewall</c> round trip) and
/// asserts BOTH halves of the P11 "Verify" block:
/// <list type="bullet">
/// <item><description>add → <c>netsh advfirewall firewall show rule
/// name=&lt;name&gt;</c> finds it;</description></item>
/// <item><description>reverse (the journaled
/// <see cref="RollbackRecord.DeleteFirewallRule"/>, the same record
/// <c>setup.exe /Uninstall</c> and a mid-install crash both invoke) → the
/// rule is gone.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>Gating:</b> reports a genuine Skipped result (via
/// <see cref="VmSystemStepsFactAttribute"/>, register row R6 — the same convention
/// as <c>PrerequisiteInstallTests</c>/<c>UpgradeInstallTests</c>) unless the
/// host is Windows, <c>SIGIL_VM_TESTS=1</c> and <c>SIGIL_VM_SYSTEMSTEPS=1</c>
/// are both set, AND the current process is elevated
/// (<see cref="Elevation.IsProcessElevated"/>) — <c>netsh advfirewall
/// firewall add/delete rule</c> requires admin. This is NOT run locally in
/// this sandbox (not Windows, not elevated, env vars unset) — the CI VM job
/// (<c>p11-system-steps-vm</c> in <c>wrapper-vm-tests.yml</c>) sets all three
/// and runs on a real elevated <c>windows-latest</c> runner.
/// </para>
/// <para>
/// Uses a uniquely-named <c>SigilItRule_*</c> rule (per-run GUID suffix) on a
/// high, unassigned local TCP port so repeat runs never collide with each
/// other or with real rules. <c>netsh</c>'s "no rules match" text (not its
/// exit code, which is not a reliable "not found" signal across Windows
/// builds) is used to assert absence, mirroring how
/// <c>RollbackRecord.DeleteFirewallRule</c>'s own undo tolerates that same
/// message. A <c>finally</c> best-effort <c>netsh ... delete rule</c>
/// guarantees the rule doesn't survive a failed assertion mid-test.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public class FirewallRuleInstallTests
{
    [VmSystemStepsFact]
    public async Task Add_then_reverse_firewall_rule_round_trip()
    {
        var ruleName = $"SigilItRule_{Guid.NewGuid():N}";

        try
        {
            var spec = new InstallStep.FirewallRule(
                Id: "it-fwrule",
                Name: ruleName,
                Direction: "in",
                Action: "allow",
                Program: null,
                Port: 51823,
                Protocol: "tcp",
                When: null,
                OnFailure: OnFailure.Continue);
            var journal = new RollbackJournal();

            var result = await new FirewallRuleStep(spec)
                .RunAsync(StepContext.Empty, journal, default);
            result.Success.Should().BeTrue(result.Error ?? "netsh advfirewall add rule should succeed under elevation");

            var afterAdd = await SystemStepProcessRunner
                .RunAsync("netsh.exe", "advfirewall", "firewall", "show", "rule", $"name={ruleName}");
            afterAdd.Stdout.Should().Contain(ruleName,
                $"netsh show rule must find '{ruleName}' right after add. stderr: {afterAdd.Stderr}");

            // Reverse via the SAME rollback record setup.exe /Uninstall and a
            // mid-install crash both invoke — proving the add+reverse pair,
            // not just the add half.
            journal.Records.Should().ContainSingle()
                .Which.Should().BeOfType<RollbackRecord.DeleteFirewallRule>()
                .Which.RuleName.Should().Be(ruleName);
            await journal.Records[0].UndoAsync(default);

            var afterUndo = await SystemStepProcessRunner
                .RunAsync("netsh.exe", "advfirewall", "firewall", "show", "rule", $"name={ruleName}");
            afterUndo.Stdout.Should().NotContain(ruleName, "the rule must be gone after rollback");
        }
        finally
        {
            await SystemStepProcessRunner
                .BestEffortAsync("netsh.exe", "advfirewall", "firewall", "delete", "rule", $"name={ruleName}");
        }
    }
}
