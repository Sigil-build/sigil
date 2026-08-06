namespace SigilBuild.Wrapper.Tests.Engine;

using System;
using System.IO;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Cli;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Tests.Helpers;
using Xunit;

/// <summary>
/// The pure seam behind register row R16's containment half. R16's other half —
/// an unresolved <c>{token}</c> in a path — lives in
/// <see cref="StepContext.ResolvePath"/> so that it covers every path-valued step
/// field rather than only the guarded destinations; it is covered by
/// <c>UnresolvedPathTokenTests</c>.
/// </summary>
public sealed class StepDestinationGuardTests
{
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
    public void The_opt_out_suppresses_containment()
    {
        using var installDir = new TempDir();
        using var elsewhere = new TempDir();

        StepDestinationGuard.Check(
                installDir.Path, "ini_write", "path", Path.Combine(elsewhere.Path, "a.ini"), true)
            .Should().BeNull();
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
