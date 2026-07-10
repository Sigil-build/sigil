using System.Linq;
using FluentAssertions;
using SigilBuild.Core.Configuration;
using SigilBuild.Core.Diagnostics;
using Xunit;

namespace SigilBuild.Core.Tests.Manifest;

/// <summary>
/// T9 parse coverage: the manifest's <c>installer.screens</c> block resolves its
/// field references against declared parameters, validates title interpolation
/// tokens and screen <c>when</c> expressions, and surfaces the SIG024x
/// diagnostics on failure.
/// </summary>
public class ScreensSchemaTests
{
    private const string Prelude = """
        spec: v1.0
        app:
          id: com.acme.Studio
          name: Acme Studio
          version: 3.2.0
          publisher: Acme, Inc.
        build:
          source: ./out
        parameters:
          server_address: { type: string, default: "https://acme.internal", install_time: true, description: "Server address" }
          license_key:    { type: secret, install_time: true, description: "License key" }
          autostart:      { type: bool,   default: true,  description: "Start when I sign in" }
          channel:        { type: enum,   values: [stable, beta, nightly], default: stable, description: "Update channel" }
        """;

    [Fact]
    public void Reference_configure_screen_parses_with_resolved_fields()
    {
        var yaml = Prelude + """

            installer:
              screens:
                - id: configure
                  title: "Configure {app.name}"
                  subtitle: "Connect to your server and set preferences."
                  fields:
                    - server_address
                    - license_key
                    - { param: channel, widget: radio }
                    - autostart
            """;

        var result = ManifestParser.Parse(yaml, "<inline>");

        result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
        var screens = result.Manifest!.Installer!.Screens!;
        screens.Should().ContainSingle();

        var configure = screens[0];
        configure.Id.Should().Be("configure");
        configure.Title.Should().Be("Configure {app.name}");
        configure.Subtitle.Should().Be("Connect to your server and set preferences.");
        configure.Fields.Select(f => f.Param).Should()
            .ContainInOrder("server_address", "license_key", "channel", "autostart");

        var channelField = configure.Fields.Single(f => f.Param == "channel");
        channelField.Widget.Should().Be("radio");
    }

    [Fact]
    public void Unknown_parameter_reference_is_a_validation_error()
    {
        var yaml = Prelude + """

            installer:
              screens:
                - id: configure
                  title: "Configure"
                  fields:
                    - server_address
                    - not_a_real_param
            """;

        var result = ManifestParser.Parse(yaml, "<inline>");

        result.Diagnostics.Should().Contain(d =>
            d.Code == DiagnosticCodes.UnknownScreenParameterRef &&
            d.Severity == DiagnosticSeverity.Error &&
            d.Message.Contains("not_a_real_param"));
    }

    [Fact]
    public void Unknown_title_interpolation_token_is_a_validation_error()
    {
        var yaml = Prelude + """

            installer:
              screens:
                - id: configure
                  title: "Configure {app.bogus}"
                  fields:
                    - server_address
            """;

        var result = ManifestParser.Parse(yaml, "<inline>");

        result.Diagnostics.Should().Contain(d =>
            d.Code == DiagnosticCodes.InvalidScreenTitleToken &&
            d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Malformed_when_expression_is_a_validation_error()
    {
        var yaml = Prelude + """

            installer:
              screens:
                - id: gated
                  title: "Gated"
                  when: "param.autostart == ("
                  fields:
                    - server_address
            """;

        var result = ManifestParser.Parse(yaml, "<inline>");

        result.Diagnostics.Should().Contain(d =>
            d.Code == DiagnosticCodes.InvalidScreenWhenExpression &&
            d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Valid_when_expression_produces_no_diagnostic()
    {
        var yaml = Prelude + """

            installer:
              screens:
                - id: gated
                  title: "Gated"
                  when: "param.autostart == true"
                  fields:
                    - server_address
            """;

        var result = ManifestParser.Parse(yaml, "<inline>");

        result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
        result.Manifest!.Installer!.Screens!.Single().When.Should().Be("param.autostart == true");
    }
}
