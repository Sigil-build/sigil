namespace SigilBuild.Wrapper.Tests.Engine;

using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Tests.Helpers;
using Xunit;

public class PrePostHookTests
{
    [Fact]
    public async Task Pre_install_failure_aborts_install_before_any_install_step_runs()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dst = new TempDir();
        var pre = new InstallStep[]
        {
            new InstallStep.RunProgram("p1", "cmd.exe", new[] { "/c", "exit", "1" },
                Wait: true, Cwd: null,
                ExpectedExitCodes: new[] { 0 },
                TimeoutSeconds: 5, When: null, OnFailure: OnFailure.Fail),
        };
        var install = new InstallStep[]
        {
            new InstallStep.DirectoryCreate("s1",
                Path: Path.Combine(dst.Path, "should-not-exist"), null, OnFailure.Fail),
        };
        var result = await new InstallEngine().RunAsync(
            pre, install, Array.Empty<InstallStep>(), StepContext.Empty);

        result.Success.Should().BeFalse();
        Directory.Exists(Path.Combine(dst.Path, "should-not-exist")).Should().BeFalse();
    }

    [Fact]
    public async Task Post_install_failure_with_continue_does_NOT_rollback_install()
    {
        using var dst = new TempDir();
        if (!OperatingSystem.IsWindows()) return;

        var pre = Array.Empty<InstallStep>();
        var install = new InstallStep[]
        {
            new InstallStep.DirectoryCreate("s1", Path.Combine(dst.Path, "appdir"),
                When: null, OnFailure: OnFailure.Fail),
        };
        var post = new InstallStep[]
        {
            new InstallStep.RunProgram("p1", "cmd.exe", new[] { "/c", "exit", "1" },
                Wait: true, Cwd: null,
                ExpectedExitCodes: new[] { 0 },
                TimeoutSeconds: 5, When: null, OnFailure: OnFailure.Continue),
        };
        var result = await new InstallEngine().RunAsync(
            pre, install, post, StepContext.Empty);

        result.Success.Should().BeTrue("post failure with continue is non-fatal");
        Directory.Exists(Path.Combine(dst.Path, "appdir")).Should().BeTrue("install was committed");
    }

    [Fact]
    public async Task Post_install_failure_with_fail_rolls_back_install()
    {
        using var dst = new TempDir();
        if (!OperatingSystem.IsWindows()) return;

        var install = new InstallStep[]
        {
            new InstallStep.DirectoryCreate("s1", Path.Combine(dst.Path, "appdir"),
                When: null, OnFailure: OnFailure.Fail),
        };
        var post = new InstallStep[]
        {
            new InstallStep.RunProgram("p1", "cmd.exe", new[] { "/c", "exit", "1" },
                Wait: true, Cwd: null,
                ExpectedExitCodes: new[] { 0 },
                TimeoutSeconds: 5, When: null, OnFailure: OnFailure.Fail),
        };
        var result = await new InstallEngine().RunAsync(
            Array.Empty<InstallStep>(), install, post, StepContext.Empty);

        result.Success.Should().BeFalse();
        Directory.Exists(Path.Combine(dst.Path, "appdir")).Should().BeFalse(
            "install rollback removed the previously-absent directory");
    }

    [Fact]
    public async Task Pre_install_runs_before_install_steps()
    {
        using var dst = new TempDir();
        var pre = new InstallStep[]
        {
            new InstallStep.DirectoryCreate("p1", Path.Combine(dst.Path, "pre-dir"),
                When: null, OnFailure: OnFailure.Fail),
        };
        var install = new InstallStep[]
        {
            new InstallStep.DirectoryCreate("s1", Path.Combine(dst.Path, "install-dir"),
                When: null, OnFailure: OnFailure.Fail),
        };
        var post = new InstallStep[]
        {
            new InstallStep.DirectoryCreate("post1", Path.Combine(dst.Path, "post-dir"),
                When: null, OnFailure: OnFailure.Fail),
        };

        var result = await new InstallEngine().RunAsync(pre, install, post, StepContext.Empty);
        result.Success.Should().BeTrue();
        Directory.Exists(Path.Combine(dst.Path, "pre-dir")).Should().BeTrue();
        Directory.Exists(Path.Combine(dst.Path, "install-dir")).Should().BeTrue();
        Directory.Exists(Path.Combine(dst.Path, "post-dir")).Should().BeTrue();
    }
}
