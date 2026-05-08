namespace SigilBuild.Wrapper.Tests.Steps;

using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Steps;
using SigilBuild.Wrapper.Tests.Helpers;
using Xunit;

public class RunProgramStepTests
{
    [Fact]
    public async Task Successful_run_returns_Ok()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var spec = new InstallStep.RunProgram(
            Id: "s",
            Program: "cmd.exe",
            Args: new[] { "/c", "exit", "0" },
            Wait: true,
            Cwd: null,
            ExpectedExitCodes: new[] { 0 },
            TimeoutSeconds: 30,
            When: null,
            OnFailure: OnFailure.Fail);

        var result = await new RunProgramStep(spec)
            .RunAsync(StepContext.Empty, new RollbackJournal(), default);

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task Unexpected_exit_code_returns_Failed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var spec = new InstallStep.RunProgram(
            Id: "s",
            Program: "cmd.exe",
            Args: new[] { "/c", "exit", "1" },
            Wait: true,
            Cwd: null,
            ExpectedExitCodes: new[] { 0 },
            TimeoutSeconds: 30,
            When: null,
            OnFailure: OnFailure.Fail);

        var result = await new RunProgramStep(spec)
            .RunAsync(StepContext.Empty, new RollbackJournal(), default);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("exited 1");
    }

    [Fact]
    public async Task Custom_expected_exit_codes_are_respected()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Exit code 3010 (reboot required) is an expected success in many installers.
        var spec = new InstallStep.RunProgram(
            Id: "s",
            Program: "cmd.exe",
            Args: new[] { "/c", "exit", "3010" },
            Wait: true,
            Cwd: null,
            ExpectedExitCodes: new[] { 0, 3010 },
            TimeoutSeconds: 30,
            When: null,
            OnFailure: OnFailure.Fail);

        var result = await new RunProgramStep(spec)
            .RunAsync(StepContext.Empty, new RollbackJournal(), default);

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task Fire_and_forget_does_not_wait_for_exit()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // ping for ~5 seconds; with Wait=false the step should return immediately.
        var spec = new InstallStep.RunProgram(
            Id: "s",
            Program: "cmd.exe",
            Args: new[] { "/c", "ping", "127.0.0.1", "-n", "5" },
            Wait: false,
            Cwd: null,
            ExpectedExitCodes: null,
            TimeoutSeconds: null,
            When: null,
            OnFailure: OnFailure.Fail);

        var sw = Stopwatch.StartNew();
        var result = await new RunProgramStep(spec)
            .RunAsync(StepContext.Empty, new RollbackJournal(), default);
        sw.Stop();

        result.Success.Should().BeTrue();
        sw.Elapsed.TotalSeconds.Should().BeLessThan(3, "fire-and-forget must not wait for the child");
    }

    [Fact]
    public async Task Timeout_kills_process()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // ping 127.0.0.1 -n 60 keeps the process alive ~60 seconds.
        var spec = new InstallStep.RunProgram(
            Id: "s",
            Program: "cmd.exe",
            Args: new[] { "/c", "ping", "127.0.0.1", "-n", "60" },
            Wait: true,
            Cwd: null,
            ExpectedExitCodes: new[] { 0 },
            TimeoutSeconds: 2,
            When: null,
            OnFailure: OnFailure.Fail);

        var sw = Stopwatch.StartNew();
        var result = await new RunProgramStep(spec)
            .RunAsync(StepContext.Empty, new RollbackJournal(), default);
        sw.Stop();

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("timed out");
        sw.Elapsed.TotalSeconds.Should().BeLessThan(10, "timeout should fire promptly");
    }

    [Fact]
    public async Task Records_no_journal_entry()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var spec = new InstallStep.RunProgram(
            Id: "s",
            Program: "cmd.exe",
            Args: new[] { "/c", "exit", "0" },
            Wait: true,
            Cwd: null,
            ExpectedExitCodes: new[] { 0 },
            TimeoutSeconds: 30,
            When: null,
            OnFailure: OnFailure.Fail);

        var journal = new RollbackJournal();
        await new RunProgramStep(spec).RunAsync(StepContext.Empty, journal, default);

        journal.Records.Should().BeEmpty(
            "run_program has no inverse and must not append a rollback record");
    }

    [Fact]
    public async Task RunProgram_with_on_failure_rollback_triggers_engine_rollback()
    {
        // Engine-level test: file_copy s1, then run_program s2 with exit=1.
        // After s2 fails, the engine walks back and undoes s1's file copies.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var src = new TempDir();
        using var dst = new TempDir();
        File.WriteAllText(Path.Combine(src.Path, "x.txt"), "1");

        var steps = new InstallStep[]
        {
            new InstallStep.FileCopy(
                Id: "s1",
                From: src.Path + "/**",
                To: dst.Path,
                Overwrite: true,
                When: null,
                OnFailure: OnFailure.Rollback),
            new InstallStep.RunProgram(
                Id: "s2",
                Program: "cmd.exe",
                Args: new[] { "/c", "exit", "1" },
                Wait: true,
                Cwd: null,
                ExpectedExitCodes: new[] { 0 },
                TimeoutSeconds: 30,
                When: null,
                OnFailure: OnFailure.Rollback),
        };

        var result = await new InstallEngine().RunAsync(steps, StepContext.Empty);

        result.Success.Should().BeFalse();
        Directory.GetFiles(dst.Path).Should().BeEmpty(
            "engine rollback should have undone the file_copy step");
    }
}
