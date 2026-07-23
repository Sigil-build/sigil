using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using SigilBuild.Core.Configuration;
using SigilBuild.Core.Diagnostics;
using SigilBuild.Core.Manifest;
using Xunit;

namespace SigilBuild.Core.Tests.Manifest;

/// <summary>
/// T11.0 (P11): the shared pack-time guard that T11.1-T11.3's machine-scope-only
/// steps (<c>scheduled_task_create</c>, <c>com_register</c>, <c>firewall_rule</c>)
/// will rely on. This task adds no system-step record — only the mechanism
/// (<see cref="InstallStep.RequiresMachineScope"/> +
/// <see cref="MachineScopeGuard"/>) and its diagnostic
/// (<see cref="DiagnosticCodes.SystemStepRequiresMachineScope"/>, SIG0310).
/// </summary>
/// <remarks>
/// <see cref="MachineScopeGuard.ValidateStep"/> is deliberately single-step
/// (step + scope + location + diagnostics): <c>ManifestParser.ParseInstallStep</c>
/// calls it once per step, right after the step record is built, passing that
/// call site's own precise node <see cref="SourceLocation"/> — the same one the
/// sibling SIG0230/SIG0231/SIG0232 diagnostics use. That is what lets a SIG0310
/// on step #40 of a 40-step list point at step #40's own YAML node rather than
/// the manifest root (line 1).
/// </remarks>
public class MachineScopeGuardTests
{
    /// <summary>
    /// Test-only step double: no real system-step record exists yet (that's
    /// T11.1's job), so the positive path is proven by subclassing the public,
    /// non-sealed <see cref="InstallStep"/> base directly and overriding
    /// <see cref="InstallStep.RequiresMachineScope"/> to true — the same
    /// override point T11.1-T11.3 will use on their own records.
    /// </summary>
    private sealed record FakeSystemStep(string Id) : InstallStep(Id, When: null, OnFailure.Fail)
    {
        public override bool RequiresMachineScope => true;
    }

    private static readonly SourceLocation RootLoc = new("s.yaml", 1, 1);

    // ---- Negative / no-op path: ordinary (non-system) steps never trip the guard,
    // regardless of scope, because their RequiresMachineScope stays the inherited
    // false default. ----

    [Theory]
    [InlineData(InstallScope.User)]
    [InlineData(InstallScope.Auto)]
    [InlineData(InstallScope.Machine)]
    public void Ordinary_steps_never_trigger_SIG0310_under_any_scope(InstallScope scope)
    {
        var diagnostics = new List<Diagnostic>();
        var mkdir = new InstallStep.DirectoryCreate("mkdir", @"C:\x", When: null, OnFailure.Fail);
        var run = new InstallStep.RunProgram("run", "setup.exe", Args: null, Wait: true, Cwd: null,
            ExpectedExitCodes: null, TimeoutSeconds: null, When: null, OnFailure.Fail);

        MachineScopeGuard.ValidateStep(mkdir, scope, RootLoc, diagnostics);
        MachineScopeGuard.ValidateStep(run, scope, RootLoc, diagnostics);

        diagnostics.Should().BeEmpty();
    }

    // ---- Positive path: a step with RequiresMachineScope = true trips the guard
    // under User and Auto, but not under Machine. ----

    [Theory]
    [InlineData(InstallScope.User)]
    [InlineData(InstallScope.Auto)]
    public void System_step_under_non_machine_scope_emits_one_SIG0310(InstallScope scope)
    {
        var diagnostics = new List<Diagnostic>();
        var step = new FakeSystemStep("register_com_server");

        MachineScopeGuard.ValidateStep(step, scope, RootLoc, diagnostics);

        diagnostics.Should().ContainSingle();
        var d = diagnostics[0];
        d.Severity.Should().Be(DiagnosticSeverity.Error);
        d.Code.Should().Be(DiagnosticCodes.SystemStepRequiresMachineScope);
        d.Code.Should().Be("SIG0310");
        d.Message.Should().Contain("register_com_server");
        d.Message.Should().Contain(nameof(FakeSystemStep));
        d.DocsUrl.Should().Be("https://docs.sigil.build/diagnostics/SIG0310");
    }

    [Fact]
    public void Machine_scope_is_a_no_op_even_for_a_machine_scope_only_step()
    {
        var diagnostics = new List<Diagnostic>();
        var step = new FakeSystemStep("register_com_server");

        MachineScopeGuard.ValidateStep(step, InstallScope.Machine, RootLoc, diagnostics);

        diagnostics.Should().BeEmpty();
    }

    // ---- Location precision (the fix): SIG0310 must point at the offending
    // step's OWN node location, never a shared/root location. This is what
    // distinguishes the fixed guard from the original per-manifest pass, which
    // reported every offense at the manifest-root SourceLocation regardless of
    // which step or collection was at fault. ----

    [Fact]
    public void ValidateStep_reports_the_steps_own_location_not_the_manifest_root()
    {
        var diagnostics = new List<Diagnostic>();
        var stepLoc = new SourceLocation("s.yaml", 42, 5);
        var step = new FakeSystemStep("late_step");

        MachineScopeGuard.ValidateStep(step, InstallScope.User, stepLoc, diagnostics);

        diagnostics.Should().ContainSingle();
        diagnostics[0].Location.Should().Be(stepLoc);
        diagnostics[0].Location.Should().NotBe(RootLoc);
    }

    [Fact]
    public void Two_offending_steps_get_their_own_distinct_locations_not_a_shared_one()
    {
        // Models exactly what ParseInstallStep does per step: call ValidateStep
        // with each step's own precise loc, not one loc shared across the whole
        // collection (or the manifest root).
        var diagnostics = new List<Diagnostic>();
        var locA = new SourceLocation("s.yaml", 10, 3);
        var locB = new SourceLocation("s.yaml", 25, 3);

        MachineScopeGuard.ValidateStep(new FakeSystemStep("a"), InstallScope.User, locA, diagnostics);
        MachineScopeGuard.ValidateStep(new FakeSystemStep("b"), InstallScope.User, locB, diagnostics);

        diagnostics.Should().HaveCount(2);
        diagnostics.Single(d => d.Message.Contains("'a'")).Location.Should().Be(locA);
        diagnostics.Single(d => d.Message.Contains("'b'")).Location.Should().Be(locB);
    }

    // ---- End-to-end through ManifestParser, using only existing (non-system)
    // step types, proving the guard is wired into the real parse path and stays
    // silent for manifests that don't need it. The genuine machine-scope-only
    // positive path end-to-end is exercised once T11.1 adds a real system step;
    // see the class doc comment. ----

    private static string Yaml(string scopeLine) => $$"""
        spec: v1.0
        app: { id: com.example.App, name: App, version: 0.1.0, publisher: P }
        build: { source: ./out }
        installer:
          {{scopeLine}}
          hooks:
            pre_install:
              - { id: hook_pre, type: run_program, program: pre.exe }
            post_install:
              - { id: hook_post, type: run_program, program: post.exe }
            pre_uninstall:
              - { id: hook_pre_u, type: run_program, program: pre_u.exe }
            post_uninstall:
              - { id: hook_post_u, type: run_program, program: post_u.exe }
        install_steps:
          - { id: mk, type: directory_create, path: "C:/x" }
        pre_install:
          - { id: pre, type: run_program, program: pre.exe }
        post_install:
          - { id: post, type: run_program, program: post.exe }
        uninstall:
          - { id: un, type: directory_delete, path: "C:/x" }
        """;

    [Theory]
    [InlineData("scope: user")]
    [InlineData("scope: auto")]
    [InlineData("scope: machine")]
    public void End_to_end_parse_of_ordinary_steps_never_emits_SIG0310(string scopeLine)
    {
        // Exercises all eight step-bearing collections (install_steps,
        // pre_install, post_install, uninstall, and the four hooks phases) with
        // only existing, non-system step types.
        var result = ManifestParser.Parse(Yaml(scopeLine), "s.yaml");

        result.Diagnostics.Should().NotContain(d => d.Code == DiagnosticCodes.SystemStepRequiresMachineScope);
    }
}
