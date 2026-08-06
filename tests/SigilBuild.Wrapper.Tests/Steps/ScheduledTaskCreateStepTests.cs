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
/// T11.1 (P11): the <c>scheduled_task_create</c> step. The exact
/// <c>schtasks.exe /Create</c> argument construction — the part that matters
/// for correctness and for the DAILY determinism question — is proven via the
/// pure <see cref="ScheduledTaskCreateStep.BuildCreateArgs"/> seam, which needs
/// neither Windows nor admin rights. The live create+query+delete leg (which
/// needs <c>/RU SYSTEM</c> elevation) is CI-VM-only; see
/// <see cref="Journal_records_DeleteScheduledTask_before_attempting_the_create"/>
/// for the one locally-runnable end-to-end assertion (journal ordering, not
/// success — this sandbox is not elevated).
/// </summary>
[SupportedOSPlatform("windows")]
public class ScheduledTaskCreateStepTests
{
    [Fact]
    public void BuildCreateArgs_logon_trigger()
    {
        var args = ScheduledTaskCreateStep.BuildCreateArgs(
            "AcmeUpdaterTask", @"C:\Program Files\Acme\updater.exe", arguments: null,
            trigger: "logon", runLevel: "limited");

        args.Should().Equal(
            "/Create", "/TN", "AcmeUpdaterTask",
            "/TR", "\"C:\\Program Files\\Acme\\updater.exe\"",
            "/SC", "ONLOGON",
            "/RL", "LIMITED",
            "/RU", "SYSTEM",
            "/F");
    }

    [Fact]
    public void BuildCreateArgs_onstart_trigger_with_highest_run_level()
    {
        var args = ScheduledTaskCreateStep.BuildCreateArgs(
            "t", "app.exe", arguments: null, trigger: "onstart", runLevel: "highest");

        args.Should().Equal(
            "/Create", "/TN", "t",
            "/TR", "\"app.exe\"",
            "/SC", "ONSTART",
            "/RL", "HIGHEST",
            "/RU", "SYSTEM",
            "/F");
    }

    [Fact]
    public void BuildCreateArgs_daily_trigger_inserts_a_fixed_deterministic_ST()
    {
        // The DAILY subtlety from the brief: schtasks /SC DAILY needs /ST. Using
        // the current wall-clock time would make two packs of the same manifest
        // schtasks-create at different times; midnight is fixed and documented.
        var args = ScheduledTaskCreateStep.BuildCreateArgs(
            "t", "app.exe", arguments: null, trigger: "daily", runLevel: "limited");

        args.Should().Equal(
            "/Create", "/TN", "t",
            "/TR", "\"app.exe\"",
            "/SC", "DAILY",
            "/ST", "00:00",
            "/RL", "LIMITED",
            "/RU", "SYSTEM",
            "/F");
    }

    [Fact]
    public void BuildCreateArgs_appends_arguments_inside_the_quoted_TR_value()
    {
        var args = ScheduledTaskCreateStep.BuildCreateArgs(
            "t", "app.exe", arguments: "--check --silent", trigger: "logon", runLevel: "limited");

        args[3].Should().Be("/TR");
        args[4].Should().Be("\"app.exe\" --check --silent");
    }

    [Fact]
    public void BuildCreateArgs_is_repeatable_and_deterministic()
    {
        // Same inputs -> byte-identical args every time (no clock, no randomness).
        var a = ScheduledTaskCreateStep.BuildCreateArgs("t", "app.exe", "--x", "daily", "highest");
        var b = ScheduledTaskCreateStep.BuildCreateArgs("t", "app.exe", "--x", "daily", "highest");

        a.Should().Equal(b);
    }

    [Fact]
    public async Task An_unanchored_program_is_refused_before_schtasks_is_ever_started()
    {
        // Was `Journal_records_DeleteScheduledTask_before_attempting_the_create`.
        // R3/R9 changed what this arrangement means: a context with no resolved
        // install_dir now fails the containment guard, so the step returns before
        // the journal entry AND before schtasks.exe is started. That is strictly
        // the safer local assertion — the old shape issued a real
        // `schtasks /Create … /RU SYSTEM` that only failed because this sandbox is
        // unelevated, and would have created a live SYSTEM task on an elevated
        // runner. Journal-before-mutation ordering is still asserted locally by
        // FirewallRuleStepTests.Journal_records_DeleteFirewallRule_before_attempting_the_add,
        // and end-to-end on the CI VM by ScheduledTaskCreateInstallTests.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var spec = new InstallStep.ScheduledTaskCreate(
            "t", "SigilTestTask_DoesNotPersist", "app.exe", Arguments: null,
            Trigger: "logon", RunLevel: "limited", When: null, OnFailure: OnFailure.Continue);
        var journal = new RollbackJournal();

        var result = await new ScheduledTaskCreateStep(spec).RunAsync(StepContext.Empty, journal, default);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("no resolved install_dir");
        journal.Records.Should().BeEmpty("nothing was attempted, so there is nothing to undo");
    }
}
