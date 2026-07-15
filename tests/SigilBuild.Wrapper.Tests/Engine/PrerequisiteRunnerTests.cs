using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Tests.Helpers;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Engine;

/// <summary>
/// P5 (gap G6): the prerequisite runner's decision logic — detect-skip, acquire →
/// run → re-detect, exit-code acceptance, 3010 reboot flag, and the scope-required
/// gate. The process launch is faked via the internal <see cref="PrerequisiteRunner.Launcher"/>
/// seam so these stay fast and OS-independent; the <c>detect</c> expression flips from
/// false to true via a real filesystem marker the fake launcher creates.
/// </summary>
public sealed class PrerequisiteRunnerTests
{
    // A StepContext with a payload root holding a dummy installer file the
    // payload:// source resolves to (the fake launcher ignores the exe itself).
    private static StepContext ContextWithPayload(string payloadRoot)
    {
        File.WriteAllBytes(Path.Combine(payloadRoot, "inst.exe"), Array.Empty<byte>());
        return new StepContext(new Dictionary<string, object?>(), payloadRoot: payloadRoot);
    }

    private static InstallerPrerequisite Prereq(
        string detect, string source = "payload://inst.exe",
        int[]? exitCodesOk = null, string? scopeRequired = null) =>
        new(Name: "Test Redist", Detect: detect, Source: source,
            Sha256: null, Args: null, ExitCodesOk: exitCodesOk,
            ScopeRequired: scopeRequired, TimeoutSeconds: null);

    // Fake launcher returning a fixed exit code and optionally creating the marker
    // file (so a subsequent detect flips to satisfied).
    private static PrerequisiteRunner.Launcher FakeLauncher(int exitCode, string? createMarker = null, Action? onRun = null)
        => (_, _, _, _) =>
        {
            onRun?.Invoke();
            if (createMarker is not null) File.WriteAllText(createMarker, "1");
            return Task.FromResult((exitCode, (string?)null));
        };

    private static string DetectMarker(string marker) => $"file_exists('{marker.Replace('\\', '/')}')";

    private static Task<PrerequisiteOutcome> Run(
        IReadOnlyList<InstallerPrerequisite> prereqs, StepContext ctx,
        PrerequisiteRunner.Launcher launcher, InstallScope scope = InstallScope.User)
        => PrerequisiteRunner.RunAsync(prereqs, ctx, scope, progress: null, launcher, CancellationToken.None);

    [Fact]
    public async Task No_prerequisites_is_success()
    {
        var outcome = await PrerequisiteRunner.RunAsync(
            null, StepContext.Empty, InstallScope.User, null, CancellationToken.None);
        outcome.Success.Should().BeTrue();
        outcome.RebootRequired.Should().BeFalse();
    }

    [Fact]
    public async Task Detect_true_skips_without_running()
    {
        using var tmp = new TempDir();
        var ctx = ContextWithPayload(tmp.Path);
        var launched = false;

        // detect uses a marker that already exists → satisfied → skip.
        var marker = Path.Combine(tmp.Path, "present.txt");
        File.WriteAllText(marker, "1");

        var outcome = await Run(
            new[] { Prereq(DetectMarker(marker)) }, ctx,
            FakeLauncher(0, onRun: () => launched = true));

        outcome.Success.Should().BeTrue();
        launched.Should().BeFalse("a satisfied detect must skip the installer entirely");
    }

    [Fact]
    public async Task Detect_false_installs_then_passes_redetect()
    {
        using var tmp = new TempDir();
        var ctx = ContextWithPayload(tmp.Path);
        var marker = Path.Combine(tmp.Path, "installed.txt"); // absent → detect false

        var outcome = await Run(
            new[] { Prereq(DetectMarker(marker)) }, ctx,
            FakeLauncher(0, createMarker: marker)); // "installs" → marker appears → re-detect true

        outcome.Success.Should().BeTrue();
        outcome.RebootRequired.Should().BeFalse();
    }

    [Fact]
    public async Task Exit_3010_flags_reboot_required()
    {
        using var tmp = new TempDir();
        var ctx = ContextWithPayload(tmp.Path);
        var marker = Path.Combine(tmp.Path, "installed.txt");

        var outcome = await Run(
            new[] { Prereq(DetectMarker(marker), exitCodesOk: new[] { 0, 3010 }) }, ctx,
            FakeLauncher(3010, createMarker: marker));

        outcome.Success.Should().BeTrue();
        outcome.RebootRequired.Should().BeTrue("an accepted 3010 flags reboot-required");
    }

    [Fact]
    public async Task Exit_3010_is_accepted_by_default_and_skips_redetect()
    {
        using var tmp = new TempDir();
        var ctx = ContextWithPayload(tmp.Path);
        var marker = Path.Combine(tmp.Path, "never.txt"); // launcher does NOT create it

        // Default exit_codes_ok ([0]); the fake exits 3010 and never satisfies detect.
        // 3010 must still be accepted (reboot) and the re-detect guard skipped.
        var outcome = await Run(new[] { Prereq(DetectMarker(marker)) }, ctx, FakeLauncher(3010));

        outcome.Success.Should().BeTrue("3010 is accepted as reboot-success even when not listed in exit_codes_ok");
        outcome.RebootRequired.Should().BeTrue();
    }

    [Fact]
    public async Task Malformed_detect_expression_aborts_with_clear_error()
    {
        using var tmp = new TempDir();
        var ctx = ContextWithPayload(tmp.Path);

        var outcome = await Run(new[] { Prereq("bogus_function('x')") }, ctx, FakeLauncher(0));

        outcome.Success.Should().BeFalse();
        outcome.Error.Should().Contain("detect expression");
    }

    [Fact]
    public async Task Exit_code_outside_ok_set_aborts_with_message()
    {
        using var tmp = new TempDir();
        var ctx = ContextWithPayload(tmp.Path);
        var marker = Path.Combine(tmp.Path, "installed.txt");

        var outcome = await Run(
            new[] { Prereq(DetectMarker(marker), exitCodesOk: new[] { 0 }) }, ctx,
            FakeLauncher(1603, createMarker: marker)); // 1603 not in ok set

        outcome.Success.Should().BeFalse();
        outcome.Error.Should().Contain("1603");
        outcome.Error.Should().Contain("Test Redist");
    }

    [Fact]
    public async Task Redetect_still_false_after_install_aborts()
    {
        using var tmp = new TempDir();
        var ctx = ContextWithPayload(tmp.Path);
        var marker = Path.Combine(tmp.Path, "never.txt"); // launcher does NOT create it

        var outcome = await Run(
            new[] { Prereq(DetectMarker(marker)) }, ctx,
            FakeLauncher(0)); // exits ok but detect stays false

        outcome.Success.Should().BeFalse();
        outcome.Error.Should().Contain("still not detected");
    }

    [Theory]
    [InlineData("allusers", InstallScope.User)]
    [InlineData("currentuser", InstallScope.Machine)]
    public async Task Scope_required_mismatch_is_a_diagnostic_before_any_run(string scopeRequired, InstallScope scope)
    {
        using var tmp = new TempDir();
        var ctx = ContextWithPayload(tmp.Path);
        var launched = false;

        var outcome = await Run(
            new[] { Prereq(DetectMarker(Path.Combine(tmp.Path, "x")), scopeRequired: scopeRequired) },
            ctx, FakeLauncher(0, onRun: () => launched = true), scope);

        outcome.Success.Should().BeFalse();
        outcome.Error.Should().Contain("Test Redist");
        launched.Should().BeFalse("a scope mismatch aborts before anything is run");
    }

    [Fact]
    public async Task Real_process_exit_code_outside_ok_set_aborts()
    {
        // End-to-end through the REAL process launcher (no seam): a copy of cmd.exe
        // acts as the fake prerequisite, exiting 1603 (not in exit_codes_ok). Robust
        // (no file paths / redirects) — `exit /b 1603` just sets the exit code.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using var tmp = new TempDir();
        var cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        File.Copy(cmd, Path.Combine(tmp.Path, "prereq.exe"));
        var ctx = new StepContext(new Dictionary<string, object?>(), payloadRoot: tmp.Path);

        var prereq = new InstallerPrerequisite(
            "Cmd Fake", DetectMarker(Path.Combine(tmp.Path, "absent.txt")), "payload://prereq.exe",
            Sha256: null, Args: new[] { "/c", "exit /b 1603" }, ExitCodesOk: new[] { 0 },
            ScopeRequired: null, TimeoutSeconds: null);

        var outcome = await PrerequisiteRunner.RunAsync(
            new[] { prereq }, ctx, InstallScope.User, progress: null, CancellationToken.None);

        outcome.Success.Should().BeFalse();
        outcome.Error.Should().Contain("1603");
    }

    [Fact]
    public async Task Missing_bundled_source_aborts_clearly()
    {
        using var tmp = new TempDir();
        var ctx = new StepContext(new Dictionary<string, object?>(), payloadRoot: tmp.Path);
        var marker = Path.Combine(tmp.Path, "installed.txt");

        var outcome = await Run(
            new[] { Prereq(DetectMarker(marker), source: "payload://does-not-exist.exe") },
            ctx, FakeLauncher(0));

        outcome.Success.Should().BeFalse();
        outcome.Error.Should().Contain("not found");
    }
}
