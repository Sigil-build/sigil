using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Cli;
using Xunit;

namespace SigilBuild.Installer.Host.Tests;

/// <summary>
/// I1: the wizard's always-on log must not carry a <see cref="ParameterType.Secret"/>
/// value. The started line used to write <c>string.Join(' ', args)</c>, and a
/// per-user install never takes the elevation branch — so R18's DPAPI handoff never
/// engages and argv still holds <c>/P&lt;secret&gt;=&lt;value&gt;</c> verbatim. That
/// contradicted docs/guides/parameters.md, which promises a secret is redacted
/// (<c>***</c>) from the install log.
/// </summary>
/// <remarks>
/// <see cref="Program.Main"/> itself is not unit-testable — <c>[STAThread]</c>, a
/// classic-desktop Avalonia lifetime, a real single-instance mutex and a real
/// stamped blob. <see cref="Program.RenderWizardStartedLine"/> is the lowest seam
/// that still produces the exact string handed to <c>InstallerLog.Info</c>, so the
/// assertion sits there rather than on the log file.
/// </remarks>
public sealed class WizardLogRedactionTests
{
    // Named and sized to stay clear of gitleaks' generic-api-key rule, which fires
    // on an identifier carrying a key/secret/token word, an equals sign, and then 10
    // or more entropic characters. Keep this identifier neutral and the literal under
    // ten characters.
    private const string PlaintextFixture = "hunter2fx";

    /// <summary>
    /// Parses through the real <see cref="CommandLineParser"/> rather than
    /// hand-building the record: <c>SecretKeys</c> and the canonical value keys must
    /// come from the same code path production uses.
    /// </summary>
    private static ParsedCommandLine ParseWithSecret(IReadOnlyList<string> args) =>
        CommandLineParser.Parse(
            args,
            new[] { ("apikey", ParameterType.Secret), ("edition", ParameterType.String) }
                .Select(p => new ParameterDefinition(
                    p.Item1,
                    p.Item2,
                    Default: null,
                    EnumValues: null,
                    InstallTime: false,
                    Description: null,
                    Pattern: null,
                    Min: null,
                    Max: null))
                .ToArray());

    [Fact]
    public void Wizard_started_line_never_carries_a_secret_parameter_value()
    {
        // Arrange — the per-user shape that leaks: no /allusers, so no elevation
        // relaunch and no handoff; the secret is sitting in argv.
        var parsed = ParseWithSecret(new[] { $"/Papikey={PlaintextFixture}", "/Pedition=pro" });

        // Act
        var line = Program.RenderWizardStartedLine(parsed);

        // Assert
        line.Should().NotContain(PlaintextFixture, "a secret parameter value must never reach the wizard log");
        line.Should().Contain("/Papikey=***", "the secret is reported as present, with its value redacted");
    }

    [Fact]
    public void Wizard_started_line_keeps_the_diagnostics_it_is_there_for()
    {
        // Arrange
        var parsed = ParseWithSecret(new[] { "/silent", $"/Papikey={PlaintextFixture}", "/Pedition=pro" });

        // Act
        var line = Program.RenderWizardStartedLine(parsed);

        // Assert — redaction must not cost the operator the rest of the line.
        line.Should().StartWith("wizard started: ");
        line.Should().Contain($"pid={Environment.ProcessId}");
        line.Should().Contain("/silent");
        line.Should().Contain("/Pedition=pro", "non-secret parameters stay legible");
        line.Should().Contain($"cwd={Environment.CurrentDirectory}");
    }
}
