using System.Linq;
using System.Text;
using FluentAssertions;
using SigilBuild.Core.Configuration;
using SigilBuild.Core.Diagnostics;
using SigilBuild.Core.Manifest;
using Xunit;

namespace SigilBuild.Core.Tests.Manifest;

/// <summary>
/// P2: parsing of <c>installer.hooks</c> (four lifecycle phases) and
/// <c>installer.run_after_install</c>, including the per-phase <c>on_failure</c>
/// defaults (fail for pre_*, continue for post_*).
/// </summary>
public class InstallerHooksParseTests
{
    private const string Header =
        "spec: v1.0\n" +
        "app: { id: com.example.App, name: App, version: 0.1.0, publisher: P }\n" +
        "build: { source: ./out }\n";

    [Fact]
    public void Parses_four_hook_phases_with_phase_specific_on_failure_defaults()
    {
        var yaml = Header +
            "installer:\n" +
            "  hooks:\n" +
            "    pre_install:\n" +
            "      - { id: p1, type: run_program, program: setup-pre.exe }\n" +
            "    post_install:\n" +
            "      - { id: q1, type: run_program, program: setup-post.exe }\n" +
            "    pre_uninstall:\n" +
            "      - { id: pu, type: run_program, program: teardown-pre.exe }\n" +
            "    post_uninstall:\n" +
            "      - { id: qu, type: run_program, program: teardown-post.exe }\n";

        var result = ManifestParser.Parse(yaml, "s.yaml");
        result.Diagnostics.Should().NotContain(d => d.Severity == DiagnosticSeverity.Error);

        var hooks = result.Manifest!.Installer!.Hooks!;
        hooks.PreInstall!.Single().OnFailure.Should().Be(OnFailure.Fail, "pre_install defaults to fail");
        hooks.PostInstall!.Single().OnFailure.Should().Be(OnFailure.Continue, "post_install defaults to continue");
        hooks.PreUninstall!.Single().OnFailure.Should().Be(OnFailure.Fail, "pre_uninstall defaults to fail");
        hooks.PostUninstall!.Single().OnFailure.Should().Be(OnFailure.Continue, "post_uninstall defaults to continue");
    }

    [Fact]
    public void Explicit_on_failure_overrides_the_phase_default()
    {
        var yaml = Header +
            "installer:\n" +
            "  hooks:\n" +
            "    pre_install:\n" +
            "      - { id: p1, type: run_program, program: x.exe, on_failure: continue }\n" +
            "    post_install:\n" +
            "      - { id: q1, type: run_program, program: y.exe, on_failure: fail }\n";

        var result = ManifestParser.Parse(yaml, "s.yaml");
        var hooks = result.Manifest!.Installer!.Hooks!;
        hooks.PreInstall!.Single().OnFailure.Should().Be(OnFailure.Continue);
        hooks.PostInstall!.Single().OnFailure.Should().Be(OnFailure.Fail);
    }

    [Fact]
    public void Parses_run_after_install_path_and_args()
    {
        var yaml = Header +
            "installer:\n" +
            "  run_after_install:\n" +
            "    path: \"{install_dir}/App.exe\"\n" +
            "    args: [\"--first-run\", \"--from={var.channel}\"]\n";

        var result = ManifestParser.Parse(yaml, "s.yaml");
        var rai = result.Manifest!.Installer!.RunAfterInstall!;
        rai.Path.Should().Be("{install_dir}/App.exe");
        rai.Args.Should().Equal("--first-run", "--from={var.channel}");
    }

    [Fact]
    public void Absent_hooks_and_launch_are_null()
    {
        var yaml = Header + "installer:\n  scope: user\n";
        var installer = ManifestParser.Parse(yaml, "s.yaml").Manifest!.Installer!;
        installer.Hooks.Should().BeNull();
        installer.RunAfterInstall.Should().BeNull();
    }
}
