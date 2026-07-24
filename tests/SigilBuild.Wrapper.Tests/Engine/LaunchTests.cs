using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Cli;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Tests.Helpers;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Engine;

/// <summary>
/// P2 (gap G4): the run-after-install launch. The token-level "de-elevate when the
/// installer ran as admin" assertion needs an elevated runner and belongs to the
/// VM matrix; here we prove the launch mechanism starts the target and that a
/// silent install starts it only with <c>/launch</c>. Windows-only (uses cmd.exe).
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class LaunchTests
{
    private static WrapperBlob LaunchBlob(string appId, string marker) => new(
        AppId: appId,
        Parameters: Array.Empty<ParameterDefinition>(),
        InstallSteps: Array.Empty<InstallStep>(),
        PreInstall: Array.Empty<InstallStep>(),
        PostInstall: Array.Empty<InstallStep>(),
        UpdateSteps: Array.Empty<InstallStep>(),
        RunAfterInstallPath: "cmd.exe",
        RunAfterInstallArgs: new[] { "/c", $"echo.>{marker}" });

    private static async Task<bool> WaitForFileAsync(string path, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (File.Exists(path)) return true;
            await Task.Delay(50);
        }
        return File.Exists(path);
    }

    private static void Cleanup(string appId)
    {
#pragma warning disable CA1031 // test cleanup best-effort
        try { UninstallStateStore.Delete(appId, InstallScope.User); } catch { }
        if (OperatingSystem.IsWindows())
        {
            try { SigilBuild.Wrapper.Cli.ArpRegistration.Remove(appId, InstallScope.User); } catch { }
        }
#pragma warning restore CA1031
    }

    [Fact]
    public async Task LaunchAppUnelevated_starts_the_run_after_install_target()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var tmp = new TempDir();
        var marker = Path.Combine(tmp.Path, "launched.txt");
        var session = InstallSession.ForTesting(
            LaunchBlob("com.acme.launch-" + Guid.NewGuid().ToString("N"), marker),
            CommandLineParser.Parse(Array.Empty<string>(), Array.Empty<ParameterDefinition>()));

        session.HasRunAfterInstall.Should().BeTrue();
        session.LaunchLabel.Should().Contain("Launch");

        session.LaunchAppUnelevated().Should().BeTrue();

        // Launcher.LaunchUnelevated only takes the plain Process.Start path
        // (Launcher.TryLaunchDirect) — a same-session, same-token spawn whose
        // side effect is reliably observable — when the current process is
        // NOT elevated. When it IS elevated, it de-elevates via the desktop
        // shell's duplicated primary token (Launcher.TryLaunchViaShellToken),
        // which can succeed (the process gets created) while the child never
        // lands in an observable context on a headless/non-interactive CI
        // runner (no matching desktop/session, no guaranteed write access to
        // this process's temp dir) — exactly the token-level behavior this
        // class's own doc comment above says belongs to the VM matrix, not a
        // unit test. So only assert the marker materializes when we know the
        // reliable direct-spawn path was taken; the de-elevation attempt
        // itself is already covered by the assertion above.
        if (Elevation.IsProcessElevated())
        {
            return; // soft-skip — de-elevation side effect belongs to the VM matrix
        }

        (await WaitForFileAsync(marker)).Should().BeTrue("the run_after_install target should have started");
    }

    [Fact]
    public async Task Silent_launch_starts_the_app_and_silent_alone_does_not()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var tmp = new TempDir();

        // /silent /launch → the app starts.
        var markerYes = Path.Combine(tmp.Path, "yes.txt");
        var appIdYes = "com.acme.launchyes-" + Guid.NewGuid().ToString("N");
        try
        {
            var parsed = CommandLineParser.Parse(new[] { "/silent", "/launch" }, Array.Empty<ParameterDefinition>());
            var session = InstallSession.ForTesting(LaunchBlob(appIdYes, markerYes), parsed);
            (await session.RunHeadlessAsync(new StringWriter(), new StringWriter())).Should().Be(0);
            (await WaitForFileAsync(markerYes)).Should().BeTrue("/silent /launch starts the app");
        }
        finally
        {
            Cleanup(appIdYes);
        }

        // /silent alone → the app does NOT start.
        var markerNo = Path.Combine(tmp.Path, "no.txt");
        var appIdNo = "com.acme.launchno-" + Guid.NewGuid().ToString("N");
        try
        {
            var parsed = CommandLineParser.Parse(new[] { "/silent" }, Array.Empty<ParameterDefinition>());
            var session = InstallSession.ForTesting(LaunchBlob(appIdNo, markerNo), parsed);
            (await session.RunHeadlessAsync(new StringWriter(), new StringWriter())).Should().Be(0);
            await Task.Delay(400); // give any errant launch time to appear
            File.Exists(markerNo).Should().BeFalse("silent without /launch must not start the app");
        }
        finally
        {
            Cleanup(appIdNo);
        }
    }
}
