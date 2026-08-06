namespace SigilBuild.Wrapper.Tests.Engine;

using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Cli;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Tests.Helpers;
using Xunit;

/// <summary>
/// The pure seam behind register row R16 — destination containment and the
/// unresolved-token failure.
/// </summary>
public sealed class StepDestinationGuardTests
{
    // ── The token scan ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(@"C:\App\{var.dest}\x", "var.dest")]
    [InlineData("{install_dir}/app.ini", "install_dir")]
    [InlineData("{scope_root}/x", "scope_root")]
    [InlineData("{temp_dir}/x", "temp_dir")]
    [InlineData("{staging_dir}/pkg.exe", "staging_dir")]
    [InlineData("{app.name}", "app.name")]
    [InlineData(@"C:\a\{_private}\b", "_private")]
    public void A_surviving_brace_token_is_reported(string value, string expected)
        => StepDestinationGuard.FirstUnresolvedToken(value).Should().Be(expected);

    [Theory]
    [InlineData(@"C:\Program Files\App\app.ini")]
    [InlineData("")]
    [InlineData(null)]
    // A braced GUID is a real directory name (driver store, COM component
    // folders) and must not be mistaken for a token: its hyphens exclude it.
    [InlineData(@"C:\App\{3f2504e0-4f89-11d3-9a0c-0305e82c3301}\x")]
    // A name starting with a digit is not an identifier.
    [InlineData(@"C:\App\{1234}\x")]
    // Braces around something that is plainly prose, not a token.
    [InlineData(@"C:\App\{not a token}\x")]
    // Unterminated: nothing to name, so nothing to refuse.
    [InlineData(@"C:\App\{install_dir")]
    // Empty braces.
    [InlineData(@"C:\App\{}\x")]
    public void Anything_that_is_not_an_identifier_in_braces_is_left_alone(string? value)
        => StepDestinationGuard.FirstUnresolvedToken(value).Should().BeNull();

    /// <summary>
    /// The cross-lane property: the scan runs over the ALREADY-RESOLVED string,
    /// so every token the engine knows has been substituted away before it is
    /// reached. That is what makes it safe against a token another lane adds —
    /// there is no allow-list here to fall out of date, and the guard never
    /// resolves anything itself (resolving <c>{staging_dir}</c> creates a
    /// directory and can throw, so a validator that resolved in order to
    /// validate would have side effects).
    /// </summary>
    [Theory]
    [InlineData("{install_dir}")]
    [InlineData("{scope_root}")]
    [InlineData("{app.name}")]
    [InlineData("{app.id}")]
    [InlineData("{temp_dir}")]
    public void No_token_the_context_knows_survives_into_the_guard(string token)
    {
        var blob = Blob();
        var ctx = StepContext.From(blob, CommandLineParser.Parse(new[] { "/silent" }, blob.Parameters));

        var resolved = ctx.ResolvePath(token + "/payload.bin");

        resolved.Should().NotContain("{");
        StepDestinationGuard.FirstUnresolvedToken(resolved).Should().BeNull(
            "a token the context substitutes can never reach the guard, so a token added by " +
            "another lane is accepted with no change here");
    }

    // ── Containment and the opt-out ───────────────────────────────────────────

    [Fact]
    public void A_destination_inside_the_install_dir_is_accepted()
    {
        using var installDir = new TempDir();

        StepDestinationGuard.Check(
                installDir.Path, "file_copy", "to", Path.Combine(installDir.Path, "bin"), false)
            .Should().BeNull();
    }

    [Fact]
    public void A_sibling_sharing_the_prefix_is_not_inside()
    {
        using var installDir = new TempDir();

        StepDestinationGuard.Check(
                installDir.Path, "file_copy", "to", installDir.Path + "-evil\\x", false)
            .Should().NotBeNull();
    }

    [Fact]
    public void The_opt_out_suppresses_containment_but_never_the_token_failure()
    {
        using var installDir = new TempDir();
        using var elsewhere = new TempDir();

        StepDestinationGuard.Check(
                installDir.Path, "ini_write", "path", Path.Combine(elsewhere.Path, "a.ini"), true)
            .Should().BeNull();

        StepDestinationGuard.Check(
                installDir.Path, "ini_write", "path", Path.Combine(elsewhere.Path, "{var.x}", "a.ini"), true)
            .Should().Contain("unresolved token");
    }

    [Fact]
    public void The_refusal_names_the_step_the_field_and_the_opt_out()
    {
        using var installDir = new TempDir();
        using var elsewhere = new TempDir();

        var message = StepDestinationGuard.Check(
            installDir.Path, "directory_delete", "path", elsewhere.Path, false);

        message.Should().Contain("directory_delete");
        message.Should().Contain("path");
        message.Should().Contain("allow_outside_install_dir");
    }

    /// <summary>
    /// The one branch that waves a destination through: a context with no
    /// resolved <c>install_dir</c>. It exists for hand-built contexts in the step
    /// unit tests. This pins the production side of that claim — every
    /// <see cref="StepContext.From"/> shape resolves one, so the branch is
    /// unreachable from a real run.
    /// </summary>
    [Theory]
    [InlineData(InstallScope.User)]
    [InlineData(InstallScope.Machine)]
    public void StepContext_From_always_resolves_an_install_dir(InstallScope scope)
    {
        var blob = Blob();

        StepContext.From(blob, CommandLineParser.Parse(new[] { "/silent" }, blob.Parameters), scope: scope)
            .InstallDir.Should().NotBeNullOrWhiteSpace();

        StepContext.From(
                Blob(appName: null),
                CommandLineParser.Parse(Array.Empty<string>(), blob.Parameters),
                scope: scope)
            .InstallDir.Should().NotBeNullOrWhiteSpace("even a blob with no app name resolves a default");
    }

    private static WrapperBlob Blob(string? appName = "Acme Studio") =>
        new(
            AppId: "com.acme.Studio",
            Parameters: Array.Empty<ParameterDefinition>(),
            InstallSteps: Array.Empty<InstallStep>(),
            PreInstall: Array.Empty<InstallStep>(),
            PostInstall: Array.Empty<InstallStep>(),
            UpdateSteps: Array.Empty<InstallStep>(),
            Scope: InstallScope.Auto,
            Options: null,
            AppName: appName,
            InstallDir: null);
}
