using System;
using System.Collections.Generic;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Cli;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Cli;

public class CommandLineParserTests
{
    private static ParameterDefinition Param(string name, ParameterType type, bool installTime = true) =>
        new(name, type, Default: null, EnumValues: null, InstallTime: installTime,
            Description: null, Pattern: null, Min: null, Max: null);

    [Fact]
    public void Parses_silent_and_named_params()
    {
        var schema = new[]
        {
            Param("Edition",     ParameterType.String),
            Param("InstallDir",  ParameterType.Path),
            Param("License",     ParameterType.Secret),
        };
        var parsed = CommandLineParser.Parse(
            new[] { "/S", "/Edition=enterprise", "/InstallDir=D:\\X", "/License=KEY12345" },
            schema);

        parsed.Silent.Should().BeTrue();
        parsed.Mode.Should().Be(WrapperMode.Install);
        parsed.Values["Edition"].Should().Be("enterprise");
        parsed.Values["InstallDir"].Should().Be(@"D:\X");
        parsed.Values["License"].Should().Be("KEY12345");
        parsed.SecretKeys.Should().ContainSingle().Which.Should().Be("License");
    }

    [Fact]
    public void Audit_safe_form_redacts_secret_values()
    {
        var schema = new[] { Param("License", ParameterType.Secret) };
        var parsed = CommandLineParser.Parse(new[] { "/License=KEY12345" }, schema);
        var audit = parsed.AuditSafeRendering();
        audit.Should().NotContain("KEY12345");
        audit.Should().Contain("License=***");
    }

    [Fact]
    public void Unknown_param_yields_named_diagnostic_not_silent_drop()
    {
        var act = () => CommandLineParser.Parse(
            new[] { "/Bogus=1" }, schema: Array.Empty<ParameterDefinition>());
        act.Should().Throw<UsageException>().WithMessage("*Bogus*not declared*");
    }

    [Theory]
    [InlineData("/edition=pro", "Edition", "pro")]
    [InlineData("/EDITION=pro", "Edition", "pro")]
    public void Param_names_are_case_insensitive(string arg, string canonical, string value)
    {
        var schema = new[] { Param("Edition", ParameterType.String) };
        var parsed = CommandLineParser.Parse(new[] { arg }, schema);
        parsed.Values[canonical].Should().Be(value);
    }

    [Fact]
    public void Update_mode_sets_Mode()
    {
        var parsed = CommandLineParser.Parse(new[] { "/Update" }, Array.Empty<ParameterDefinition>());
        parsed.Mode.Should().Be(WrapperMode.Update);
    }

    [Fact]
    public void Uninstall_mode_sets_Mode()
    {
        var parsed = CommandLineParser.Parse(new[] { "/Uninstall" }, Array.Empty<ParameterDefinition>());
        parsed.Mode.Should().Be(WrapperMode.Uninstall);
    }

    [Fact]
    public void Last_value_wins_for_duplicate_param()
    {
        var schema = new[] { Param("Edition", ParameterType.String) };
        var parsed = CommandLineParser.Parse(new[] { "/Edition=a", "/Edition=b" }, schema);
        parsed.Values["Edition"].Should().Be("b");
    }

    [Fact]
    public void Bare_positional_arg_is_rejected()
    {
        var act = () => CommandLineParser.Parse(
            new[] { "positional" }, Array.Empty<ParameterDefinition>());
        act.Should().Throw<UsageException>();
    }
}
