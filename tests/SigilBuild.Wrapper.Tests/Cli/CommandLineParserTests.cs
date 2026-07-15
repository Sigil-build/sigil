using System;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Cli;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Cli;

public class CommandLineParserTests
{
    private static ParameterDefinition Param(
        string name, ParameterType type, object? @default = null, bool installTime = true) =>
        new(name, type, Default: @default, EnumValues: null, InstallTime: installTime,
            Description: null, Pattern: null, Min: null, Max: null);

    // ── Silent aliases ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("/silent")]
    [InlineData("/S")]
    [InlineData("/s")]
    [InlineData("/SILENT")]
    public void Silent_and_its_S_alias_parse_identically(string flag)
    {
        var parsed = CommandLineParser.Parse(new[] { flag }, Array.Empty<ParameterDefinition>());
        parsed.Silent.Should().BeTrue();
        parsed.VerySilent.Should().BeFalse();
        parsed.Mode.Should().Be(WrapperMode.Install);
    }

    [Fact]
    public void VerySilent_implies_silent()
    {
        var parsed = CommandLineParser.Parse(new[] { "/verysilent" }, Array.Empty<ParameterDefinition>());
        parsed.Silent.Should().BeTrue();
        parsed.VerySilent.Should().BeTrue();
    }

    // ── /P parameter overrides ────────────────────────────────────────────────

    [Fact]
    public void Parses_silent_and_P_prefixed_params()
    {
        var schema = new[]
        {
            Param("Edition",     ParameterType.String, @default: "community"),
            Param("InstallDir",  ParameterType.Path,   @default: "C:\\Default"),
            Param("License",     ParameterType.Secret, @default: "n/a"),
        };
        var parsed = CommandLineParser.Parse(
            new[] { "/S", "/PEdition=enterprise", "/PInstallDir=D:\\X", "/PLicense=KEY12345" },
            schema);

        parsed.Silent.Should().BeTrue();
        parsed.Mode.Should().Be(WrapperMode.Install);
        parsed.Values["Edition"].Should().Be("enterprise");
        parsed.Values["InstallDir"].Should().Be(@"D:\X");
        parsed.Values["License"].Should().Be("KEY12345");
        parsed.SecretKeys.Should().ContainSingle().Which.Should().Be("License");
    }

    [Theory]
    [InlineData("/Pedition=pro", "Edition", "pro")]
    [InlineData("/PEDITION=pro", "Edition", "pro")]
    public void Param_names_are_case_insensitive(string arg, string canonical, string value)
    {
        var schema = new[] { Param("Edition", ParameterType.String, @default: "x") };
        var parsed = CommandLineParser.Parse(new[] { arg }, schema);
        parsed.Values[canonical].Should().Be(value);
    }

    [Fact]
    public void Last_value_wins_for_duplicate_param()
    {
        var schema = new[] { Param("Edition", ParameterType.String, @default: "x") };
        var parsed = CommandLineParser.Parse(new[] { "/PEdition=a", "/PEdition=b" }, schema);
        parsed.Values["Edition"].Should().Be("b");
    }

    [Fact]
    public void Unknown_P_name_yields_named_diagnostic_and_exit64()
    {
        var act = () => CommandLineParser.Parse(
            new[] { "/Pfoo=bar" }, schema: Array.Empty<ParameterDefinition>());
        act.Should().Throw<UsageException>().WithMessage("*foo*neither a declared parameter nor a built-in option*");
    }

    // ── Built-in option overrides (T8 model; parsed + stored here) ─────────────

    [Fact]
    public void Known_option_override_is_stored_not_rejected()
    {
        var parsed = CommandLineParser.Parse(
            new[] { "/S", "/Pdesktop_shortcut=false", "/Padd_to_path=true" },
            Array.Empty<ParameterDefinition>());
        parsed.Options["desktop_shortcut"].Should().Be("false");
        parsed.Options["add_to_path"].Should().Be("true");
    }

    // ── /D install-dir + scope flags (parse + store only) ─────────────────────

    [Fact]
    public void D_flag_captures_install_dir()
    {
        var parsed = CommandLineParser.Parse(new[] { "/D=C:\\Tools\\Acme" }, Array.Empty<ParameterDefinition>());
        parsed.InstallDir.Should().Be(@"C:\Tools\Acme");
    }

    [Fact]
    public void D_flag_without_path_is_usage_error()
    {
        var act = () => CommandLineParser.Parse(new[] { "/D=" }, Array.Empty<ParameterDefinition>());
        act.Should().Throw<UsageException>();
    }

    [Theory]
    [InlineData("/allusers", ScopeOverride.AllUsers)]
    [InlineData("/currentuser", ScopeOverride.CurrentUser)]
    public void Scope_flags_are_stored(string flag, ScopeOverride expected)
    {
        var parsed = CommandLineParser.Parse(new[] { flag }, Array.Empty<ParameterDefinition>());
        parsed.Scope.Should().Be(expected);
    }

    // ── Mode flags ────────────────────────────────────────────────────────────

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

    // ── Required-parameter enforcement in silent install ──────────────────────

    [Fact]
    public void Silent_install_missing_required_param_exit64_naming_it()
    {
        var schema = new[] { Param("license_key", ParameterType.Secret, @default: null) };
        var act = () => CommandLineParser.Parse(new[] { "/silent" }, schema);
        act.Should().Throw<UsageException>().WithMessage("*license_key*");
    }

    [Fact]
    public void Silent_install_required_param_supplied_is_ok()
    {
        var schema = new[] { Param("license_key", ParameterType.Secret, @default: null) };
        var parsed = CommandLineParser.Parse(new[] { "/silent", "/Plicense_key=XYZ" }, schema);
        parsed.Values["license_key"].Should().Be("XYZ");
    }

    [Fact]
    public void Interactive_mode_does_not_enforce_required_params()
    {
        // No /silent: the wizard collects the missing value, so parsing must not throw.
        var schema = new[] { Param("license_key", ParameterType.Secret, @default: null) };
        var parsed = CommandLineParser.Parse(Array.Empty<string>(), schema);
        parsed.Silent.Should().BeFalse();
        parsed.Values.Should().NotContainKey("license_key");
    }

    // ── Audit-safe rendering redacts secrets ──────────────────────────────────

    [Fact]
    public void Audit_safe_form_redacts_secret_values()
    {
        var schema = new[] { Param("License", ParameterType.Secret, @default: "n/a") };
        var parsed = CommandLineParser.Parse(new[] { "/PLicense=KEY12345" }, schema);
        var audit = parsed.AuditSafeRendering();
        audit.Should().NotContain("KEY12345");
        audit.Should().Contain("/PLicense=***");
    }

    // ── /LOG install logging (P7) ─────────────────────────────────────────────

    [Theory]
    [InlineData("/LOG")]
    [InlineData("/log")]
    public void Bare_LOG_requests_logging_with_default_path(string flag)
    {
        var parsed = CommandLineParser.Parse(new[] { flag }, Array.Empty<ParameterDefinition>());
        parsed.LogRequested.Should().BeTrue();
        parsed.LogPath.Should().BeNull("a bare /LOG defers to the session's default %TEMP% path");
    }

    [Theory]
    [InlineData("/LOG=C:\\Temp\\setup.log", @"C:\Temp\setup.log")]
    [InlineData("/log=out.log", "out.log")]
    public void LOG_with_path_captures_explicit_path(string flag, string expected)
    {
        var parsed = CommandLineParser.Parse(new[] { flag }, Array.Empty<ParameterDefinition>());
        parsed.LogRequested.Should().BeTrue();
        parsed.LogPath.Should().Be(expected);
    }

    [Fact]
    public void LOG_equals_without_path_is_usage_error()
    {
        var act = () => CommandLineParser.Parse(new[] { "/LOG=" }, Array.Empty<ParameterDefinition>());
        act.Should().Throw<UsageException>();
    }

    [Fact]
    public void LOG_prefixed_junk_is_still_rejected()
    {
        // /LOGGING is NOT /LOG — the closed grammar rejects it (exit 64).
        var act = () => CommandLineParser.Parse(new[] { "/LOGGING" }, Array.Empty<ParameterDefinition>());
        act.Should().Throw<UsageException>();
    }

    [Fact]
    public void No_LOG_flag_means_logging_not_requested()
    {
        var parsed = CommandLineParser.Parse(new[] { "/silent" }, Array.Empty<ParameterDefinition>());
        parsed.LogRequested.Should().BeFalse();
        parsed.LogPath.Should().BeNull();
    }

    [Fact]
    public void Audit_safe_form_includes_LOG_flag()
    {
        var withPath = CommandLineParser.Parse(new[] { "/LOG=x.log" }, Array.Empty<ParameterDefinition>());
        withPath.AuditSafeRendering().Should().Contain("/LOG=x.log");

        var bare = CommandLineParser.Parse(new[] { "/silent", "/LOG" }, Array.Empty<ParameterDefinition>());
        bare.AuditSafeRendering().Should().Contain("/LOG");
    }

    // ── /launch run-after-install (P2) ────────────────────────────────────────

    [Theory]
    [InlineData("/launch")]
    [InlineData("/LAUNCH")]
    public void Launch_flag_is_parsed(string flag)
    {
        var parsed = CommandLineParser.Parse(new[] { "/silent", flag }, Array.Empty<ParameterDefinition>());
        parsed.Launch.Should().BeTrue();
    }

    [Fact]
    public void No_launch_flag_means_launch_false()
    {
        CommandLineParser.Parse(new[] { "/silent" }, Array.Empty<ParameterDefinition>())
            .Launch.Should().BeFalse();
    }

    [Fact]
    public void Audit_safe_form_includes_launch()
    {
        CommandLineParser.Parse(new[] { "/silent", "/launch" }, Array.Empty<ParameterDefinition>())
            .AuditSafeRendering().Should().Contain("/launch");
    }

    // ── Closed grammar rejects junk ───────────────────────────────────────────

    [Fact]
    public void Bare_positional_arg_is_rejected()
    {
        var act = () => CommandLineParser.Parse(
            new[] { "positional" }, Array.Empty<ParameterDefinition>());
        act.Should().Throw<UsageException>();
    }

    [Fact]
    public void Unrecognized_slash_flag_is_rejected()
    {
        var act = () => CommandLineParser.Parse(
            new[] { "/bogusflag" }, Array.Empty<ParameterDefinition>());
        act.Should().Throw<UsageException>();
    }
}
