// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

namespace SigilBuild.Wrapper.Tests.Engine;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Cli;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Tests.Helpers;
using Xunit;

/// <summary>
/// Running an <c>uninstall.exe</c> whose application is already gone is not a failure:
/// there is simply nothing installed. The engine reports an ABSENT state as such, the
/// silent path exits <c>0</c> with a log line, and the interactive path returns a
/// not-installed outcome for the window's neutral screen — while a state that EXISTS and
/// is refused still fails exactly as before, because reporting a refusal as an absence
/// would hide it (R1). (R107)
/// </summary>
public sealed class NotInstalledUninstallTests
{
    private static string NewAppId() => "com.acme.r107-" + Guid.NewGuid().ToString("N");

    private static WrapperBlob Blob(string appId, IReadOnlyList<InstallStep>? preUninstall = null)
        => new(
            AppId: appId,
            Parameters: Array.Empty<ParameterDefinition>(),
            InstallSteps: Array.Empty<InstallStep>(),
            PreInstall: Array.Empty<InstallStep>(),
            PostInstall: Array.Empty<InstallStep>(),
            UpdateSteps: Array.Empty<InstallStep>(),
            HookPreUninstall: preUninstall);

    private static InstallStep.DirectoryCreate Mkdir(string id, string path)
        => new(id, path, When: null, OnFailure.Continue) { AllowOutsideInstallDir = true };

    private static InstallSession Session(string appId, string log, IReadOnlyList<InstallStep>? preUninstall = null)
    {
        var blob = Blob(appId, preUninstall);
        var parsed = CommandLineParser.Parse(
            new[] { "/S", "/Uninstall", "/currentuser", $"/LOG={log}" }, blob.Parameters);
        return InstallSession.ForTesting(blob, parsed);
    }

    // A present-but-unreadable state file: Load refuses it (R19), it is not absent.
    private static void PlantUnreadableState(string appId)
    {
        var path = UninstallStateStore.PathFor(appId, InstallScope.User);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "this is not json at all");
    }

    private static void Cleanup(string appId)
    {
#pragma warning disable CA1031 // test cleanup best-effort
        try { Directory.Delete(UninstallStateStore.DirectoryFor(appId, InstallScope.User), recursive: true); } catch { }
#pragma warning restore CA1031
    }

    [Fact]
    public async Task The_engine_reports_absent_state_as_not_installed()
    {
        // Arrange
        using var installDir = new TempDir();

        // Act
        var result = await new UninstallEngine().RunAsync(
            NewAppId(), installDir.Path, SignedDeclarations.None);

        // Assert
        result.Success.Should().BeFalse();
        result.NotInstalled.Should().BeTrue();
        result.Error.Should().Contain("no uninstall state found");
    }

    [WindowsFact]
    public async Task The_engine_never_reports_refused_state_as_not_installed()
    {
        // Arrange
        using var installDir = new TempDir();
        var appId = NewAppId();
        PlantUnreadableState(appId);
        try
        {
            // Act
            var result = await new UninstallEngine().RunAsync(
                appId, installDir.Path, SignedDeclarations.None, InstallScope.User);

            // Assert
            result.Success.Should().BeFalse();
            result.NotInstalled.Should().BeFalse("a refusal is not an absence (R1)");
            result.Error.Should().Contain("REFUSED");
        }
        finally
        {
            Cleanup(appId);
        }
    }

    [Fact]
    public async Task A_silent_uninstall_of_an_app_that_is_not_installed_exits_0_and_says_so()
    {
        // Arrange
        using var tmp = new TempDir();
        var log = Path.Combine(tmp.Path, "uninstall.log");
        var hookMarker = Path.Combine(tmp.Path, "hook-ran");
        var appId = NewAppId();
        var session = Session(appId, log, preUninstall: new[] { Mkdir("quit", hookMarker) });
        using var output = new StringWriter();
        using var error = new StringWriter();

        // Act
        var code = await session.RunHeadlessAsync(output, error);

        // Assert
        code.Should().Be(0, "nothing installed is not a failure");
        output.ToString().Should().Contain("is not installed");
        error.ToString().Should().BeEmpty();
        File.ReadAllText(log).Should().Contain("result: not installed").And.Contain("exit code: 0");
        Directory.Exists(hookMarker).Should().BeFalse("no hook runs when there is no application to act on");
    }

    [WindowsFact]
    public async Task A_silent_uninstall_over_refused_state_still_fails()
    {
        // Arrange
        using var tmp = new TempDir();
        var log = Path.Combine(tmp.Path, "uninstall.log");
        var appId = NewAppId();
        PlantUnreadableState(appId);
        var session = Session(appId, log);
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            // Act
            var code = await session.RunHeadlessAsync(output, error);

            // Assert
            code.Should().Be(1);
            error.ToString().Should().Contain("REFUSED");
            File.ReadAllText(log).Should().Contain("result: uninstall failed").And.NotContain("not installed");
        }
        finally
        {
            Cleanup(appId);
        }
    }

    [Fact]
    public async Task An_interactive_uninstall_of_an_app_that_is_not_installed_returns_a_not_installed_outcome()
    {
        // Arrange
        using var tmp = new TempDir();
        var appId = NewAppId();
        var session = Session(appId, Path.Combine(tmp.Path, "uninstall.log"));

        // Act
        var notInstalled = session.IsNotInstalled();
        var outcome = await session.RunUninstallInteractiveAsync(progress: null);

        // Assert
        notInstalled.Should().BeTrue("the window opens on its not-installed screen");
        outcome.NotInstalled.Should().BeTrue();
        outcome.IsBlocked.Should().BeFalse();
    }

    [WindowsFact]
    public async Task An_interactive_uninstall_over_refused_state_is_a_failure_not_an_absence()
    {
        // Arrange
        using var tmp = new TempDir();
        var appId = NewAppId();
        PlantUnreadableState(appId);
        var session = Session(appId, Path.Combine(tmp.Path, "uninstall.log"));
        try
        {
            // Act
            var notInstalled = session.IsNotInstalled();
            var outcome = await session.RunUninstallInteractiveAsync(progress: null);

            // Assert
            notInstalled.Should().BeFalse("the state file is present");
            outcome.NotInstalled.Should().BeFalse();
            outcome.Success.Should().BeFalse();
            outcome.Error.Should().Contain("REFUSED");
        }
        finally
        {
            Cleanup(appId);
        }
    }

    [Fact]
    public void An_installed_app_is_not_reported_as_not_installed()
    {
        // Arrange
        using var tmp = new TempDir();
        var appId = NewAppId();
        UninstallStateStore.Save(appId, new RollbackJournal(), InstallScope.User, Array.Empty<string>());
        var session = Session(appId, Path.Combine(tmp.Path, "uninstall.log"));
        try
        {
            // Act / Assert
            session.IsNotInstalled().Should().BeFalse();
        }
        finally
        {
            Cleanup(appId);
        }
    }
}
