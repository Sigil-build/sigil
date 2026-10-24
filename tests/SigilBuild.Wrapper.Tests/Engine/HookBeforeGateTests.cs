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
/// The pre-phase hooks run before the files-in-use gate, so a publisher's teardown
/// hook (an app's own <c>--quit</c>) gets its chance to stop a running application
/// before the gate refuses the run; and a silent run that is refused leaves a log
/// naming what blocked it even without <c>/LOG</c>. (R93)
/// </summary>
/// <remarks>
/// A held named mutex stands in for the running application: it blocks the gate for
/// the whole run, so the hook having run at all — its marker directory exists, and
/// its log line precedes the blocker line — is what proves the order.
/// </remarks>
public sealed class HookBeforeGateTests
{
    private static InstallStep.DirectoryCreate Mkdir(string id, string path)
        => new(id, path, When: null, OnFailure.Continue) { AllowOutsideInstallDir = true };

    private static WrapperBlob Blob(
        string appId,
        IReadOnlyList<InstallStep> install,
        IReadOnlyList<InstallStep>? preInstall = null,
        IReadOnlyList<InstallStep>? preUninstall = null,
        IReadOnlyList<string>? appMutex = null)
        => new(
            AppId: appId,
            Parameters: Array.Empty<ParameterDefinition>(),
            InstallSteps: install,
            PreInstall: Array.Empty<InstallStep>(),
            PostInstall: Array.Empty<InstallStep>(),
            UpdateSteps: Array.Empty<InstallStep>(),
            HookPreInstall: preInstall,
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
    public async Task Silent_install_runs_pre_install_hooks_before_the_files_in_use_gate()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Arrange
        using var tmp = new TempDir();
        var log = Path.Combine(tmp.Path, "install.log");
        var marker = Path.Combine(tmp.Path, "quit-ran");
        var body = Path.Combine(tmp.Path, "bodydir");
        var mutexName = "Local\\sigil-test-r93i-" + Guid.NewGuid().ToString("N");
        var appId = "com.acme.r93install-" + Guid.NewGuid().ToString("N");
        using var held = new Mutex(initiallyOwned: true, mutexName);
        var blob = Blob(appId,
            install: new[] { Mkdir("body", body) },
            preInstall: new[] { Mkdir("quit-running", marker) },
            appMutex: new[] { mutexName });
        var parsed = CommandLineParser.Parse(new[] { "/silent", $"/LOG={log}" }, blob.Parameters);
        var session = InstallSession.ForTesting(blob, parsed);

        try
        {
            // Act
            var code = await session.RunHeadlessAsync(new StringWriter(), new StringWriter());

            // Assert
            code.Should().Be(InstallSession.FilesInUseExitCode, "the mutex is still held after the hook");
            Directory.Exists(marker).Should().BeTrue("the teardown hook runs before the gate refuses");
            Directory.Exists(body).Should().BeFalse("a blocked run still never opens the journal");

            var text = File.ReadAllText(log);
            var iHook = text.IndexOf("hook pre_install", StringComparison.Ordinal);
            var iBlocked = text.IndexOf("blocked by:", StringComparison.Ordinal);
            iHook.Should().BeGreaterThan(-1);
            iBlocked.Should().BeGreaterThan(iHook, "the gate scans only after the hook phase");
        }
        finally
        {
            Cleanup(appId);
        }
    }

    [Fact]
    public async Task Silent_uninstall_runs_pre_uninstall_hooks_before_the_files_in_use_gate()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Arrange
        using var tmp = new TempDir();
        var log = Path.Combine(tmp.Path, "uninstall.log");
        var marker = Path.Combine(tmp.Path, "quit-ran");
        var mutexName = "Local\\sigil-test-r93u-" + Guid.NewGuid().ToString("N");
        var appId = "com.acme.r93uninstall-" + Guid.NewGuid().ToString("N");
        UninstallStateStore.Save(appId, new RollbackJournal(), InstallScope.User, Array.Empty<string>());
        using var held = new Mutex(initiallyOwned: true, mutexName);
        var blob = Blob(appId,
            install: Array.Empty<InstallStep>(),
            preUninstall: new[] { Mkdir("quit-running", marker) },
            appMutex: new[] { mutexName });
        var parsed = CommandLineParser.Parse(new[] { "/silent", "/Uninstall", $"/LOG={log}" }, blob.Parameters);
        var session = InstallSession.ForTesting(blob, parsed);

        try
        {
            // Act
            var code = await session.RunHeadlessAsync(new StringWriter(), new StringWriter());

            // Assert
            code.Should().Be(InstallSession.FilesInUseExitCode);
            Directory.Exists(marker).Should().BeTrue("the teardown hook runs before the gate refuses");
            UninstallStateStore.TryLoad(appId, InstallScope.User)
                .Should().NotBeNull("a blocked uninstall still removes nothing");

            var text = File.ReadAllText(log);
            var iHook = text.IndexOf("hook pre_uninstall", StringComparison.Ordinal);
            var iBlocked = text.IndexOf("blocked by:", StringComparison.Ordinal);
            iHook.Should().BeGreaterThan(-1);
            iBlocked.Should().BeGreaterThan(iHook);
        }
        finally
        {
            Cleanup(appId);
        }
    }

    [Fact]
    public async Task A_refused_silent_run_without_LOG_writes_the_blockers_to_the_diagnostic_log()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Arrange — what Apps & features' silent command leaves behind. (R93)
        using var tmp = new TempDir();
        var diagnostic = Path.Combine(tmp.Path, "sigil-wizard-test.log");
        var mutexName = "Local\\sigil-test-r93d-" + Guid.NewGuid().ToString("N");
        var appId = "com.acme.r93diag-" + Guid.NewGuid().ToString("N");
        UninstallStateStore.Save(appId, new RollbackJournal(), InstallScope.User, Array.Empty<string>());
        using var held = new Mutex(initiallyOwned: true, mutexName);
        var blob = Blob(appId, install: Array.Empty<InstallStep>(), appMutex: new[] { mutexName });
        var parsed = CommandLineParser.Parse(new[] { "/S", "/Uninstall", "/currentuser" }, blob.Parameters);
        var session = InstallSession.ForTesting(blob, parsed);
        session.DiagnosticLogPath = diagnostic;

        try
        {
            // Act
            var code = await session.RunHeadlessAsync(new StringWriter(), new StringWriter());

            // Assert
            code.Should().Be(InstallSession.FilesInUseExitCode);
            File.Exists(diagnostic).Should().BeTrue("a refused silent run must leave a trace");
            var text = File.ReadAllText(diagnostic);
            text.Should().Contain(mutexName, "the log names the blocker");
            text.Should().Contain($"exit code: {InstallSession.FilesInUseExitCode}");
            session.LogFilePath.Should().BeNull("the diagnostic log is not the /LOG file the Failed screen opens");
        }
        finally
        {
            Cleanup(appId);
        }
    }

    [Fact]
    public async Task Pre_install_hooks_run_by_the_wizard_gate_are_not_run_again_by_the_install()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Arrange
        using var tmp = new TempDir();
        var log = Path.Combine(tmp.Path, "once.log");
        var marker = Path.Combine(tmp.Path, "quit-ran");
        var body = Path.Combine(tmp.Path, "bodydir");
        var appId = "com.acme.r93once-" + Guid.NewGuid().ToString("N");
        var blob = Blob(appId,
            install: new[] { Mkdir("body", body) },
            preInstall: new[] { Mkdir("quit-running", marker) });
        var parsed = CommandLineParser.Parse(new[] { $"/LOG={log}" }, blob.Parameters);
        var session = InstallSession.ForTesting(blob, parsed);

        try
        {
            // Act
            var hooksOk = await session.RunPreInstallHooksAsync(null, null, progress: null);
            var outcome = await session.RunInstallCoreAsync(Array.Empty<byte>(), progress: null);

            // Assert
            session.HasPreInstallHooks.Should().BeTrue();
            hooksOk.Should().BeTrue();
            outcome.Success.Should().BeTrue();
            Directory.Exists(body).Should().BeTrue();
            Regex.Count(File.ReadAllText(log), "hook pre_install")
                .Should().Be(1, "the phase runs at most once per install run");
        }
        finally
        {
            Cleanup(appId);
        }
    }

    [Fact]
    public async Task A_pre_install_hook_that_aborts_at_the_wizard_gate_fails_the_install_without_running_it()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Arrange
        using var tmp = new TempDir();
        var body = Path.Combine(tmp.Path, "bodydir");
        var appId = "com.acme.r93abort-" + Guid.NewGuid().ToString("N");
        var failing = new InstallStep.FileCopy(
            "quit-running",
            Path.Combine(tmp.Path, "nope-" + Guid.NewGuid().ToString("N"), "*"),
            Path.Combine(tmp.Path, "dst"),
            Overwrite: false, When: null, OnFailure.Fail);
        var blob = Blob(appId, install: new[] { Mkdir("body", body) }, preInstall: new[] { failing });
        var parsed = CommandLineParser.Parse(Array.Empty<string>(), blob.Parameters);
        var session = InstallSession.ForTesting(blob, parsed);

        try
        {
            // Act
            var hooksOk = await session.RunPreInstallHooksAsync(null, null, progress: null);
            var outcome = await session.RunInstallCoreAsync(Array.Empty<byte>(), progress: null);

            // Assert
            hooksOk.Should().BeFalse();
            outcome.Success.Should().BeFalse();
            outcome.Error.Should().Contain("pre_install hook 'quit-running' failed");
            Directory.Exists(body).Should().BeFalse("the journal never opens after an aborted hook phase");
        }
        finally
        {
            Cleanup(appId);
        }
    }

    [Fact]
    public async Task Without_pre_install_hooks_the_wizard_gate_call_is_a_no_op()
    {
        // Arrange
        var appId = "com.acme.r93none-" + Guid.NewGuid().ToString("N");
        var blob = Blob(appId, install: Array.Empty<InstallStep>());
        var session = InstallSession.ForTesting(blob, CommandLineParser.Parse(Array.Empty<string>(), blob.Parameters));

        // Act
        var hooksOk = await session.RunPreInstallHooksAsync(null, null, progress: null);

        // Assert
        session.HasPreInstallHooks.Should().BeFalse();
        hooksOk.Should().BeTrue();
    }
}
