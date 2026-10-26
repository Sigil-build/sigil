// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Cli;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Tests.Helpers;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Engine;

/// <summary>
/// The interactive uninstall — the one Apps &amp; features opens — runs the same
/// files-in-use gate as the silent path, after the <c>pre_uninstall</c> hooks and
/// before any journal replay. A blocked run reports the blockers and removes
/// nothing; running it again after they are cleared resumes it without running the
/// hooks a second time. (R103)
/// </summary>
/// <remarks>
/// A held named mutex stands in for the running application. Disposing it closes
/// the only handle, so the named object disappears and the next scan is clear — from
/// any thread, which matters because the test resumes after an await.
/// </remarks>
public sealed class InteractiveUninstallGateTests
{
    private static InstallStep.DirectoryCreate Mkdir(string id, string path)
        => new(id, path, When: null, OnFailure.Continue) { AllowOutsideInstallDir = true };

    private static WrapperBlob Blob(
        string appId,
        IReadOnlyList<InstallStep>? preUninstall = null,
        IReadOnlyList<string>? appMutex = null)
        => new(
            AppId: appId,
            Parameters: Array.Empty<ParameterDefinition>(),
            InstallSteps: Array.Empty<InstallStep>(),
            PreInstall: Array.Empty<InstallStep>(),
            PostInstall: Array.Empty<InstallStep>(),
            UpdateSteps: Array.Empty<InstallStep>(),
            HookPreUninstall: preUninstall,
            AppMutex: appMutex);

    private static void Cleanup(string appId)
    {
#pragma warning disable CA1031 // test cleanup best-effort
        try { UninstallStateStore.Delete(appId, InstallScope.User); } catch { }
        if (OperatingSystem.IsWindows())
        {
            try { ArpRegistration.Remove(appId, InstallScope.User); } catch { }
        }
#pragma warning restore CA1031
    }

    [Fact]
    public async Task A_blocked_interactive_uninstall_removes_nothing_and_names_the_blockers()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Arrange
        using var tmp = new TempDir();
        var log = Path.Combine(tmp.Path, "uninstall.log");
        var marker = Path.Combine(tmp.Path, "quit-ran");
        var mutexName = "Local\\sigil-test-r103b-" + Guid.NewGuid().ToString("N");
        var appId = "com.acme.r103blocked-" + Guid.NewGuid().ToString("N");
        UninstallStateStore.Save(appId, new RollbackJournal(), InstallScope.User, Array.Empty<string>());
        using var held = new Mutex(initiallyOwned: true, mutexName);
        var blob = Blob(appId,
            preUninstall: new[] { Mkdir("quit-running", marker) },
            appMutex: new[] { mutexName });
        var parsed = CommandLineParser.Parse(new[] { "/Uninstall", $"/LOG={log}" }, blob.Parameters);
        var session = InstallSession.ForTesting(blob, parsed);

        try
        {
            // Act
            var outcome = await session.RunUninstallInteractiveAsync(progress: null);

            // Assert
            outcome.Success.Should().BeFalse();
            outcome.IsBlocked.Should().BeTrue();
            outcome.Blockers.Should().ContainSingle()
                .Which.Describe().Should().Contain(mutexName, "the outcome names the application");
            Directory.Exists(marker).Should().BeTrue("the teardown hook runs before the gate");
            UninstallStateStore.TryLoad(appId, InstallScope.User)
                .Should().NotBeNull("the gate refuses before the replay starts, so nothing is removed");

            var text = File.ReadAllText(log);
            text.IndexOf("blocked by:", StringComparison.Ordinal)
                .Should().BeGreaterThan(text.IndexOf("hook pre_uninstall", StringComparison.Ordinal));
            text.Should().NotContain("result: uninstall", "no replay ran, so no replay result was logged");
        }
        finally
        {
            Cleanup(appId);
        }
    }

    [Fact]
    public async Task A_resumed_interactive_uninstall_replays_without_running_the_hooks_again()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Arrange
        using var tmp = new TempDir();
        var log = Path.Combine(tmp.Path, "uninstall.log");
        var marker = Path.Combine(tmp.Path, "quit-ran");
        var mutexName = "Local\\sigil-test-r103r-" + Guid.NewGuid().ToString("N");
        var appId = "com.acme.r103resume-" + Guid.NewGuid().ToString("N");
        UninstallStateStore.Save(appId, new RollbackJournal(), InstallScope.User, Array.Empty<string>());
        var held = new Mutex(initiallyOwned: true, mutexName);
        var blob = Blob(appId,
            preUninstall: new[] { Mkdir("quit-running", marker) },
            appMutex: new[] { mutexName });
        var parsed = CommandLineParser.Parse(new[] { "/Uninstall", $"/LOG={log}" }, blob.Parameters);
        var session = InstallSession.ForTesting(blob, parsed);

        try
        {
            var first = await session.RunUninstallInteractiveAsync(progress: null);
            first.IsBlocked.Should().BeTrue();
            session.ScanUninstallBlockers().Should().NotBeEmpty("the wizard's Retry sees what the gate saw");

            // Act — the user closes the application, then the wizard resumes.
            held.Dispose();
            session.ScanUninstallBlockers().Should().BeEmpty();
            var second = await session.RunUninstallInteractiveAsync(progress: null);

            // Assert
            second.Success.Should().BeTrue(second.Error);
            second.IsBlocked.Should().BeFalse();
            UninstallStateStore.TryLoad(appId, InstallScope.User)
                .Should().BeNull("the resumed run replays the journal");
            var text = File.ReadAllText(log);
            Regex.Count(text, "hook pre_uninstall")
                .Should().Be(1, "the hook phase runs at most once per session");
            text.Should().Contain("result: uninstall success");
        }
        finally
        {
            held.Dispose();
            Cleanup(appId);
        }
    }

    [Fact]
    public async Task An_interactive_uninstall_with_nothing_running_replays_on_the_first_call()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Arrange
        var appId = "com.acme.r103clear-" + Guid.NewGuid().ToString("N");
        UninstallStateStore.Save(appId, new RollbackJournal(), InstallScope.User, Array.Empty<string>());
        var blob = Blob(appId, appMutex: new[] { "Local\\sigil-test-r103c-" + Guid.NewGuid().ToString("N") });
        var parsed = CommandLineParser.Parse(new[] { "/Uninstall" }, blob.Parameters);
        var session = InstallSession.ForTesting(blob, parsed);

        try
        {
            // Act
            var outcome = await session.RunUninstallInteractiveAsync(progress: null);

            // Assert
            outcome.Success.Should().BeTrue(outcome.Error);
            outcome.Blockers.Should().BeEmpty();
            UninstallStateStore.TryLoad(appId, InstallScope.User).Should().BeNull();
        }
        finally
        {
            Cleanup(appId);
        }
    }
}
