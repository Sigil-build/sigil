using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Configuration;
using SigilBuild.Core.Diagnostics;
using SigilBuild.Core.Manifest;
using Xunit;

namespace SigilBuild.Core.Tests.Manifest;

public class InstallStepsSchemaTests
{
    private const string ValidPrelude = """
        spec: v1.0
        app:
          id: com.example.App
          name: Example
          version: 1.0.0
          publisher: Example Inc.
        build:
          source: ./out
        """;

    [Theory]
    [InlineData("file_copy")]
    [InlineData("directory_create")]
    [InlineData("file_delete")]
    [InlineData("directory_delete")]
    [InlineData("registry_write")]
    [InlineData("registry_delete_value")]
    [InlineData("registry_delete_key")]
    [InlineData("shortcut_create")]
    [InlineData("env_set")]
    [InlineData("run_program")]
    public async Task MUST_tier_step_type_is_accepted(string stepType)
    {
        // Single shared YAML carries every required field for every MUST-tier step;
        // the typed deserializer only consumes the ones relevant to the dispatched
        // step kind (other keys produce SIG0231 warnings, not errors).
        var yaml = $$"""
            {{ValidPrelude}}
            install_steps:
              - id: s1
                type: {{stepType}}
                from: a
                to: b
                path: a
                hive: HKLM
                key: K
                name: NAME
                target: a
                location: start_menu
                program: a
                value: V
                type_value: REG_SZ
            """;
        var diagnostics = await ManifestLoader.ValidateAsync(yaml);
        diagnostics.Should().NotContain(d => d.Code == DiagnosticCodes.UnknownStepType);
        diagnostics.Should().NotContain(d => d.Code == DiagnosticCodes.MissingRequiredStepField);
    }

    [Fact]
    public async Task Unknown_step_type_is_rejected()
    {
        var yaml = $$"""
            {{ValidPrelude}}
            install_steps:
              - id: s1
                type: nuke_disk
            """;
        var diagnostics = await ManifestLoader.ValidateAsync(yaml);
        diagnostics.Should().Contain(d => d.Code.StartsWith("SIG02"));
    }

    [Fact]
    public async Task When_clause_accepts_an_arbitrary_string()
    {
        var yaml = $$"""
            {{ValidPrelude}}
            install_steps:
              - id: s1
                type: directory_create
                path: a
                when: "parameters.edition == 'pro'"
            """;
        var diagnostics = await ManifestLoader.ValidateAsync(yaml);
        diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
    }

    [Fact]
    public async Task Pre_and_post_install_blocks_are_accepted()
    {
        var yaml = $$"""
            {{ValidPrelude}}
            pre_install:
              - id: pre1
                type: directory_create
                path: a
            post_install:
              - id: post1
                type: run_program
                program: cmd.exe
                args: ["/c", "echo", "hi"]
            """;
        var diagnostics = await ManifestLoader.ValidateAsync(yaml);
        diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
    }

    [Fact]
    public async Task Manifest_without_install_steps_keeps_fields_null()
    {
        var yaml = ValidPrelude;
        var diagnostics = await ManifestLoader.ValidateAsync(yaml);
        diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();

        var parsed = ManifestParser.Parse(yaml, "<inline>");
        parsed.Manifest!.InstallSteps.Should().BeNull();
        parsed.Manifest!.PreInstall.Should().BeNull();
        parsed.Manifest!.PostInstall.Should().BeNull();
    }

    [Fact]
    public async Task Step_missing_required_field_emits_SIG0232()
    {
        var yaml = $$"""
            {{ValidPrelude}}
            install_steps:
              - id: s1
                type: file_copy
                from: a
            """;
        var diagnostics = await ManifestLoader.ValidateAsync(yaml);
        diagnostics.Should().Contain(d => d.Code == DiagnosticCodes.MissingRequiredStepField);
    }

    [Fact]
    public async Task Typed_graph_round_trips_each_step_kind()
    {
        var yaml = $$"""
            {{ValidPrelude}}
            install_steps:
              - id: cp
                type: file_copy
                from: payload/**
                to: C:\\App
              - id: mk
                type: directory_create
                path: C:\\App\\logs
              - id: rm
                type: file_delete
                path: C:\\App\\old.txt
                if_missing: ignore
              - id: rmdir
                type: directory_delete
                path: C:\\App\\tmp
                recursive: true
              - id: regw
                type: registry_write
                hive: HKLM
                key: Software\\Example
                name: Version
                type_value: REG_SZ
                value: "1.0"
              - id: regdv
                type: registry_delete_value
                hive: HKCU
                key: Software\\Example
                name: Stale
              - id: regdk
                type: registry_delete_key
                hive: HKCU
                key: Software\\Example\\Old
                recursive: true
              - id: sc
                type: shortcut_create
                target: C:\\App\\app.exe
                location: start_menu
                name: Example
              - id: env
                type: env_set
                name: APP_HOME
                value: C:\\App
                scope: machine
              - id: run
                type: run_program
                program: C:\\App\\post.exe
                args: ["--init"]
            """;

        var parsed = ManifestParser.Parse(yaml, "<inline>");
        parsed.Manifest!.InstallSteps.Should().NotBeNull();
        parsed.Manifest!.InstallSteps!.Should().HaveCount(10);
        parsed.Manifest!.InstallSteps![0].Should().BeOfType<InstallStep.FileCopy>();
        parsed.Manifest!.InstallSteps![1].Should().BeOfType<InstallStep.DirectoryCreate>();
        parsed.Manifest!.InstallSteps![2].Should().BeOfType<InstallStep.FileDelete>();
        parsed.Manifest!.InstallSteps![3].Should().BeOfType<InstallStep.DirectoryDelete>();
        parsed.Manifest!.InstallSteps![4].Should().BeOfType<InstallStep.RegistryWrite>();
        parsed.Manifest!.InstallSteps![5].Should().BeOfType<InstallStep.RegistryDeleteValue>();
        parsed.Manifest!.InstallSteps![6].Should().BeOfType<InstallStep.RegistryDeleteKey>();
        parsed.Manifest!.InstallSteps![7].Should().BeOfType<InstallStep.ShortcutCreate>();
        parsed.Manifest!.InstallSteps![8].Should().BeOfType<InstallStep.EnvSet>();
        parsed.Manifest!.InstallSteps![9].Should().BeOfType<InstallStep.RunProgram>();
    }
}
