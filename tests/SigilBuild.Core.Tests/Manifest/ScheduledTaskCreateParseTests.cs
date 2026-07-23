using System.Linq;
using FluentAssertions;
using SigilBuild.Core.Configuration;
using SigilBuild.Core.Diagnostics;
using SigilBuild.Core.Manifest;
using Xunit;

namespace SigilBuild.Core.Tests.Manifest;

/// <summary>
/// T11.1 (P11): parsing of the <c>scheduled_task_create</c> install step — the
/// first of three machine-scope-only "system steps". Covers the happy path,
/// SIG0232 (missing required field) for <c>name</c>/<c>program</c>/<c>trigger</c>,
/// SIG0233 (bad enum value) for <c>trigger</c>/<c>run_level</c>, the
/// <c>run_level</c> default (<c>limited</c>), and the FIRST real positive
/// SIG0310 (<see cref="DiagnosticCodes.SystemStepRequiresMachineScope"/>) case:
/// this step overrides <see cref="InstallStep.RequiresMachineScope"/> to
/// <c>true</c>, so a manifest that isn't pinned to <c>scope: machine</c> must be
/// refused at pack time.
/// </summary>
public class ScheduledTaskCreateParseTests
{
    private static string Yaml(string step, string scopeLine = "scope: machine") =>
        "spec: v1.0\n" +
        "app: { id: com.example.App, name: App, version: 0.1.0, publisher: P }\n" +
        "build: { source: ./out }\n" +
        "installer:\n  " + scopeLine + "\n" +
        "install_steps:\n  - " + step + "\n";

    private const string HappyStep =
        "{ id: t, type: scheduled_task_create, name: AcmeUpdater, " +
        "program: \"{install_dir}/updater.exe\", arguments: \"--check\", trigger: daily, run_level: highest }";

    [Fact]
    public void Parses_happy_path()
    {
        var result = ManifestParser.Parse(Yaml(HappyStep), "s.yaml");
        result.Diagnostics.Should().NotContain(d => d.Severity == DiagnosticSeverity.Error);

        var step = result.Manifest!.InstallSteps!.OfType<InstallStep.ScheduledTaskCreate>().Single();
        step.Name.Should().Be("AcmeUpdater");
        step.Program.Should().Be("{install_dir}/updater.exe");
        step.Arguments.Should().Be("--check");
        step.Trigger.Should().Be("daily");
        step.RunLevel.Should().Be("highest");
        step.RequiresMachineScope.Should().BeTrue();
    }

    [Fact]
    public void Run_level_defaults_to_limited_when_absent()
    {
        var step =
            "{ id: t, type: scheduled_task_create, name: AcmeUpdater, " +
            "program: updater.exe, trigger: logon }";

        var result = ManifestParser.Parse(Yaml(step), "s.yaml");
        result.Diagnostics.Should().NotContain(d => d.Severity == DiagnosticSeverity.Error);

        var parsed = result.Manifest!.InstallSteps!.OfType<InstallStep.ScheduledTaskCreate>().Single();
        parsed.RunLevel.Should().Be("limited");
        parsed.Arguments.Should().BeNull("arguments is optional");
    }

    [Theory]
    [InlineData("{ id: t, type: scheduled_task_create, program: updater.exe, trigger: logon }")] // no name
    [InlineData("{ id: t, type: scheduled_task_create, name: AcmeUpdater, trigger: logon }")]    // no program
    [InlineData("{ id: t, type: scheduled_task_create, name: AcmeUpdater, program: updater.exe }")] // no trigger
    public void Missing_required_field_emits_SIG0232(string step)
    {
        var result = ManifestParser.Parse(Yaml(step), "s.yaml");
        result.Diagnostics.Should().Contain(d => d.Code == DiagnosticCodes.MissingRequiredStepField);
        result.Manifest!.InstallSteps!.OfType<InstallStep.ScheduledTaskCreate>().Should().BeEmpty();
    }

    [Fact]
    public void Unknown_trigger_value_emits_SIG0233()
    {
        var step =
            "{ id: t, type: scheduled_task_create, name: AcmeUpdater, program: updater.exe, trigger: weekly }";

        var result = ManifestParser.Parse(Yaml(step), "s.yaml");
        var d = result.Diagnostics.Should().ContainSingle(d => d.Code == DiagnosticCodes.InvalidStepFieldValue).Which;
        d.Severity.Should().Be(DiagnosticSeverity.Error);
        d.Message.Should().Contain("trigger");
        d.Message.Should().Contain("weekly");
        result.Manifest!.InstallSteps!.OfType<InstallStep.ScheduledTaskCreate>().Should().BeEmpty();
    }

    [Fact]
    public void Unknown_run_level_value_emits_SIG0233()
    {
        var step =
            "{ id: t, type: scheduled_task_create, name: AcmeUpdater, program: updater.exe, " +
            "trigger: onstart, run_level: admin }";

        var result = ManifestParser.Parse(Yaml(step), "s.yaml");
        var d = result.Diagnostics.Should().ContainSingle(d => d.Code == DiagnosticCodes.InvalidStepFieldValue).Which;
        d.Severity.Should().Be(DiagnosticSeverity.Error);
        d.Message.Should().Contain("run_level");
        d.Message.Should().Contain("admin");
        result.Manifest!.InstallSteps!.OfType<InstallStep.ScheduledTaskCreate>().Should().BeEmpty();
    }

    // ---- SIG0310: the first real positive case (T11.0 only had a fake test
    // double). `scope: user` / `scope: auto` must trip it, pointing at this
    // step's own node; `scope: machine` must not. ----

    [Theory]
    [InlineData("scope: user")]
    [InlineData("scope: auto")]
    public void User_or_auto_scope_trips_SIG0310_pointing_at_the_step(string scopeLine)
    {
        var result = ManifestParser.Parse(Yaml(HappyStep, scopeLine), "s.yaml");

        var d = result.Diagnostics.Should()
            .ContainSingle(d => d.Code == DiagnosticCodes.SystemStepRequiresMachineScope).Which;
        d.Severity.Should().Be(DiagnosticSeverity.Error);
        d.Message.Should().Contain("'t'");
        // Points at the step's own node (line 5: installer: / scope line / install_steps: / - step),
        // never line 1 (the manifest root).
        d.Location.Line.Should().BeGreaterThan(1);
    }

    [Fact]
    public void Machine_scope_does_not_trip_SIG0310()
    {
        var result = ManifestParser.Parse(Yaml(HappyStep, "scope: machine"), "s.yaml");

        result.Diagnostics.Should().NotContain(d => d.Code == DiagnosticCodes.SystemStepRequiresMachineScope);
    }
}
