// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using System.Linq;
using FluentAssertions;
using SigilBuild.Core.Configuration;
using SigilBuild.Core.Diagnostics;
using SigilBuild.Core.Manifest;
using Xunit;

namespace SigilBuild.Core.Tests.Manifest;

/// <summary>
/// <c>on_failure</c> is validated against the abort mode its phase can actually
/// deliver (R78). A journalled phase has only <c>rollback</c>; a hook, which runs
/// outside the journal, has only <c>fail</c>. Each family refuses the other's word
/// with <c>SIG0233</c> instead of silently aliasing it, and so does a misspelling —
/// the old parser turned every unrecognised word into <c>fail</c>.
/// </summary>
public class OnFailurePolicyParseTests
{
    private const string Header =
        "spec: v1.0\n" +
        "app: { id: com.example.App, name: App, version: 0.1.0, publisher: P }\n" +
        "build: { source: ./out }\n";

    private static string JournalledManifest(string onFailureClause) =>
        Header +
        "install_steps:\n" +
        $"  - {{ id: s1, type: directory_create, path: '{{install_dir}}/x'{onFailureClause} }}\n";

    private static string HookManifest(string onFailureClause) =>
        Header +
        "installer:\n" +
        "  hooks:\n" +
        "    pre_install:\n" +
        $"      - {{ id: h1, type: run_program, program: x.exe{onFailureClause} }}\n";

    private static ParseResult Parse(string yaml) => ManifestParser.Parse(yaml, "s.yaml");

    // ---- journalled phases -------------------------------------------------

    [Fact]
    public void Journalled_step_defaults_to_rollback()
    {
        // Arrange
        var yaml = JournalledManifest(string.Empty);

        // Act
        var result = Parse(yaml);

        // Assert
        result.Diagnostics.Should().NotContain(d => d.Severity == DiagnosticSeverity.Error);
        result.Manifest!.InstallSteps!.Single().OnFailure.Should().Be(OnFailure.Rollback,
            "an unwinding abort is the only abort a journalled phase has, so it is the default");
    }

    [Theory]
    [InlineData("rollback", OnFailure.Rollback)]
    [InlineData("continue", OnFailure.Continue)]
    public void Journalled_step_accepts_its_own_two_modes(string word, OnFailure expected)
    {
        // Arrange
        var yaml = JournalledManifest($", on_failure: {word}");

        // Act
        var result = Parse(yaml);

        // Assert
        result.Diagnostics.Should().NotContain(d => d.Severity == DiagnosticSeverity.Error);
        result.Manifest!.InstallSteps!.Single().OnFailure.Should().Be(expected);
    }

    [Fact]
    public void Journalled_step_refuses_fail_and_says_why()
    {
        // Arrange — 'fail' promised an abort without rollback that never happened.
        var yaml = JournalledManifest(", on_failure: fail");

        // Act
        var result = Parse(yaml);

        // Assert
        var error = result.Diagnostics.Should().ContainSingle(d =>
            d.Severity == DiagnosticSeverity.Error &&
            d.Code == DiagnosticCodes.InvalidStepFieldValue).Subject;
        error.Message.Should().Contain("s1").And.Contain("rollback",
            "the message has to name the value to use instead, not merely reject one");
    }

    [Theory]
    [InlineData("rollbck")]
    [InlineData("Rollback")]
    [InlineData("abort")]
    public void Journalled_step_refuses_an_unrecognised_word(string word)
    {
        // Arrange — each of these used to parse silently as OnFailure.Fail.
        var yaml = JournalledManifest($", on_failure: {word}");

        // Act
        var result = Parse(yaml);

        // Assert
        result.Diagnostics.Should().Contain(d =>
            d.Severity == DiagnosticSeverity.Error &&
            d.Code == DiagnosticCodes.InvalidStepFieldValue);
    }

    // ---- hooks -------------------------------------------------------------

    [Theory]
    [InlineData("fail", OnFailure.Fail)]
    [InlineData("continue", OnFailure.Continue)]
    public void Hook_step_accepts_its_own_two_modes(string word, OnFailure expected)
    {
        // Arrange
        var yaml = HookManifest($", on_failure: {word}");

        // Act
        var result = Parse(yaml);

        // Assert
        result.Diagnostics.Should().NotContain(d => d.Severity == DiagnosticSeverity.Error);
        result.Manifest!.Installer!.Hooks!.PreInstall!.Single().OnFailure.Should().Be(expected);
    }

    [Fact]
    public void Hook_step_refuses_rollback_and_says_why()
    {
        // Arrange — a hook has no journal, so 'rollback' promised an unwind that
        // cannot happen; the parser used to accept it and quietly mean 'fail'.
        var yaml = HookManifest(", on_failure: rollback");

        // Act
        var result = Parse(yaml);

        // Assert
        var error = result.Diagnostics.Should().ContainSingle(d =>
            d.Severity == DiagnosticSeverity.Error &&
            d.Code == DiagnosticCodes.InvalidStepFieldValue).Subject;
        error.Message.Should().Contain("h1").And.Contain("fail");
    }

    [Fact]
    public void Hook_phase_defaults_are_unchanged_by_the_journalled_default()
    {
        // Arrange — pre_* defaults to fail, post_* to continue. Moving the
        // journalled default to rollback must not disturb either.
        var yaml = Header +
            "installer:\n" +
            "  hooks:\n" +
            "    pre_install:\n" +
            "      - { id: h1, type: run_program, program: x.exe }\n" +
            "    post_install:\n" +
            "      - { id: h2, type: run_program, program: y.exe }\n";

        // Act
        var result = Parse(yaml);

        // Assert
        result.Diagnostics.Should().NotContain(d => d.Severity == DiagnosticSeverity.Error);
        var hooks = result.Manifest!.Installer!.Hooks!;
        hooks.PreInstall!.Single().OnFailure.Should().Be(OnFailure.Fail);
        hooks.PostInstall!.Single().OnFailure.Should().Be(OnFailure.Continue);
    }
}
