using System.Linq;
using FluentAssertions;
using SigilBuild.Core.Configuration;
using SigilBuild.Core.Diagnostics;
using SigilBuild.Core.Manifest;
using Xunit;

namespace SigilBuild.Core.Tests.Manifest;

/// <summary>
/// T11.2 (P11): parsing of the <c>com_register</c> install step — the second of
/// three machine-scope-only "system steps" and the one AOT-risk step in P11.
/// Covers the happy path, SIG0232 (missing required <c>path</c>) — com_register
/// has no enum-valued fields so SIG0233 does not apply — and the positive
/// SIG0310 (<see cref="DiagnosticCodes.SystemStepRequiresMachineScope"/>) case:
/// this step overrides <see cref="InstallStep.RequiresMachineScope"/> to
/// <c>true</c>, so a manifest not pinned to <c>scope: machine</c> is refused at
/// pack time.
/// </summary>
public class ComRegisterParseTests
{
    private static string Yaml(string step, string scopeLine = "scope: machine") =>
        "spec: v1.0\n" +
        "app: { id: com.example.App, name: App, version: 0.1.0, publisher: P }\n" +
        "build: { source: ./out }\n" +
        "installer:\n  " + scopeLine + "\n" +
        "install_steps:\n  - " + step + "\n";

    private const string HappyStep =
        "{ id: reg, type: com_register, path: \"{install_dir}/Acme.Shell.dll\" }";

    [Fact]
    public void Parses_happy_path()
    {
        var result = ManifestParser.Parse(Yaml(HappyStep), "s.yaml");
        result.Diagnostics.Should().NotContain(d => d.Severity == DiagnosticSeverity.Error);

        var step = result.Manifest!.InstallSteps!.OfType<InstallStep.ComRegister>().Single();
        step.Id.Should().Be("reg");
        step.Path.Should().Be("{install_dir}/Acme.Shell.dll");
        step.RequiresMachineScope.Should().BeTrue();
    }

    [Fact]
    public void Missing_path_emits_SIG0232()
    {
        var step = "{ id: reg, type: com_register }";

        var result = ManifestParser.Parse(Yaml(step), "s.yaml");

        var d = result.Diagnostics.Should()
            .ContainSingle(d => d.Code == DiagnosticCodes.MissingRequiredStepField).Which;
        d.Severity.Should().Be(DiagnosticSeverity.Error);
        d.Message.Should().Contain("path");
        result.Manifest!.InstallSteps!.OfType<InstallStep.ComRegister>().Should().BeEmpty();
    }

    // ---- SIG0310: com_register is machine-scope-only. scope: user / scope:
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
        d.Message.Should().Contain("'reg'");
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
