// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
/// Only an application a user can close blocks an install or uninstall. A Windows
/// service, a critical process or the shell holding a file is a system holder: the gate
/// waits briefly for it, then continues and logs it — it never opens the
/// Close-applications step, never yields exit code 4, and the close path never asks the
/// Restart Manager to stop it. (R115)
/// </summary>
/// <remarks>
/// The Restart Manager result is injected through <see cref="FilesInUseProbe"/>, so the
/// classification, the settle loop and the exit-code decision are exercised without a
/// real process holding a file.
/// </remarks>
public sealed class FilesInUseClassificationTests
{
    private static readonly RmHolder DiagTrack =
        new(4452, "Connected User Experiences and Telemetry", RmAppType.Service, "DiagTrack");

    private static readonly RmHolder Csrss = new(612, "csrss.exe", RmAppType.Critical);

    private static readonly RmHolder Shell = new(7720, "Windows Explorer", RmAppType.Explorer);

    private static readonly RmHolder Dictum = new(9001, "Dictum", RmAppType.MainWindow);

    // ── Classification ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(0u, true)]     // RmUnknownApp
    [InlineData(1u, true)]     // RmMainWindow
    [InlineData(2u, true)]     // RmOtherWindow
    [InlineData(5u, true)]     // RmConsole
    [InlineData(3u, false)]    // RmService
    [InlineData(4u, false)]    // RmExplorer
    [InlineData(1000u, false)] // RmCritical
    public void Only_applications_a_user_can_close_are_user_applications(uint rmAppType, bool expected)
    {
        // Arrange
        var type = (RmAppType)rmAppType;

        // Act
        var actual = FilesInUse.IsUserApplication(type);

        // Assert
        actual.Should().Be(expected);
    }

    [Fact]
    public void A_service_only_result_has_no_blockers_and_lists_the_service_as_a_system_holder()
    {
        // Arrange
        var holders = new[] { DiagTrack };

        // Act
        var scan = FilesInUse.Classify(Array.Empty<AppBlocker>(), holders);

        // Assert
        scan.Blockers.Should().BeEmpty();
        scan.SystemHolders.Should().ContainSingle().Which.Should().Be(DiagTrack);
    }

    [Fact]
    public void A_mixed_result_reports_only_the_user_application_and_the_held_mutex_as_blockers()
    {
        // Arrange
        var mutex = new AppBlocker("Local\\dictum-running", 0, FromMutex: true);
        var holders = new[] { DiagTrack, Dictum, Csrss, Shell };

        // Act
        var scan = FilesInUse.Classify(new[] { mutex }, holders);

        // Assert
        scan.Blockers.Should().Equal(mutex, new AppBlocker("Dictum", 9001, FromMutex: false));
        scan.SystemHolders.Should().Equal(DiagTrack, Csrss, Shell);
    }

    [Fact]
    public void Duplicate_entries_are_reported_once()
    {
        // Arrange
        var holders = new[] { Dictum, Dictum, DiagTrack, DiagTrack };

        // Act
        var scan = FilesInUse.Classify(Array.Empty<AppBlocker>(), holders);

        // Assert
        scan.Blockers.Should().ContainSingle();
        scan.SystemHolders.Should().ContainSingle();
    }

    [Fact]
    public void A_system_holder_is_described_with_its_kind_service_name_and_pid()
    {
        // Arrange / Act
        var line = DiagTrack.Describe();

        // Assert
        line.Should().Be("Connected User Experiences and Telemetry [DiagTrack] (system service, pid 4452)");
    }

    // ── Settle loop ──────────────────────────────────────────────────────────

    [Fact]
    public async Task A_system_holder_that_lets_go_is_waited_out_and_the_result_is_clear()
    {
        // Arrange
        var results = new Queue<FilesInUseScan>(new[]
        {
            ServiceOnly(), ServiceOnly(), FilesInUseScan.Clear,
        });
        var waits = new List<TimeSpan>();

        // Act
        var scan = await FilesInUse.SettleAsync(
            results.Dequeue, FilesInUse.DefaultSettleDelays, Recorder(waits), CancellationToken.None);

        // Assert
        scan.Blockers.Should().BeEmpty();
        scan.SystemHolders.Should().BeEmpty();
        waits.Should().Equal(TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public async Task A_system_holder_that_persists_is_returned_after_the_whole_schedule()
    {
        // Arrange
        var scans = 0;
        var waits = new List<TimeSpan>();

        // Act
        var scan = await FilesInUse.SettleAsync(
            () => { scans++; return ServiceOnly(); },
            FilesInUse.DefaultSettleDelays,
            Recorder(waits),
            CancellationToken.None);

        // Assert
        scan.Blockers.Should().BeEmpty();
        scan.SystemHolders.Should().ContainSingle().Which.Should().Be(DiagTrack);
        scans.Should().Be(FilesInUse.DefaultSettleDelays.Count + 1);
        waits.Aggregate(TimeSpan.Zero, (a, b) => a + b).Should().Be(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task A_user_blocker_is_returned_at_once_without_waiting()
    {
        // Arrange
        var waits = new List<TimeSpan>();
        var mixed = FilesInUse.Classify(Array.Empty<AppBlocker>(), new[] { DiagTrack, Dictum });

        // Act
        var scan = await FilesInUse.SettleAsync(
            () => mixed, FilesInUse.DefaultSettleDelays, Recorder(waits), CancellationToken.None);

        // Assert
        scan.Blockers.Should().ContainSingle().Which.Name.Should().Be("Dictum");
        waits.Should().BeEmpty();
    }

    [Fact]
    public async Task A_clear_result_is_returned_at_once_without_waiting()
    {
        // Arrange
        var waits = new List<TimeSpan>();

        // Act
        var scan = await FilesInUse.SettleAsync(
            () => FilesInUseScan.Clear, FilesInUse.DefaultSettleDelays, Recorder(waits), CancellationToken.None);

        // Assert
        scan.Should().BeSameAs(FilesInUseScan.Clear);
        waits.Should().BeEmpty();
    }

    // ── Close path ───────────────────────────────────────────────────────────

    [Fact]
    public void The_close_path_never_targets_a_service_a_critical_process_or_the_shell()
    {
        // Arrange
        var console = new RmHolder(77, "dictum-cli.exe", RmAppType.Console);
        var holders = new[] { DiagTrack, Dictum, Csrss, Shell, console };

        // Act
        var targets = FilesInUse.SelectShutdownTargets(holders);

        // Assert
        targets.Should().Equal(Dictum, console);
        targets.Should().OnlyContain(t => t.IsUserApplication);
    }

    [Fact]
    public void The_close_path_has_nothing_to_shut_down_when_only_system_holders_remain()
    {
        // Arrange
        var holders = new[] { DiagTrack, Csrss, Shell };

        // Act
        var targets = FilesInUse.SelectShutdownTargets(holders);

        // Assert
        targets.Should().BeEmpty();
    }

    /// <summary>
    /// The real sweep reads each entry's application type: a separate console process
    /// holding a file under the directory comes back as a blocker, not as a system
    /// holder. (R115)
    /// </summary>
    [WindowsFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task A_real_console_process_holding_a_file_is_classified_as_a_blocker()
    {
        // Arrange
        using var tmp = new TempDir();
        using var flags = new TempDir(); // the ready flag lives OUTSIDE the scanned dir
        var target = Path.Combine(tmp.Path, "locked.txt");
        File.WriteAllText(target, "payload");
        using var holder = FileHolderProcess.Start(target, Path.Combine(flags.Path, "ready"));
        var pid = (uint)holder.Id;

        // Act
        var scan = FilesInUseScan.Clear;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        do
        {
            scan = FilesInUse.ScanDetailed(null, tmp.Path, Array.Empty<string>());
            if (scan.Blockers.Any(b => b.ProcessId == pid))
            {
                break;
            }
            await Task.Delay(250);
        }
        while (DateTime.UtcNow < deadline);

        // Assert
        scan.Blockers.Should().Contain(b => b.ProcessId == pid && !b.FromMutex);
        scan.SystemHolders.Should().NotContain(h => h.ProcessId == pid);
    }

    // ── The gate, end to end through the session ─────────────────────────────

    [Fact]
    public async Task A_service_only_result_never_blocks_a_silent_install_and_is_logged()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Arrange
        using var tmp = new TempDir();
        var log = Path.Combine(tmp.Path, "install.log");
        var body = Path.Combine(tmp.Path, "bodydir");
        var appId = "com.acme.r115svc-" + Guid.NewGuid().ToString("N");
        var probe = new FakeProbe(_ => ServiceOnly());
        var blob = InstallBlob(appId, body);
        var parsed = CommandLineParser.Parse(new[] { "/silent", $"/LOG={log}" }, blob.Parameters);
        var session = InstallSession.ForTesting(blob, parsed);
        session.FilesInUseProbeOverride = probe.Build();

        try
        {
            // Act
            var code = await session.RunHeadlessAsync(new StringWriter(), new StringWriter());

            // Assert
            code.Should().Be(0, "a system service is never a blocker");
            code.Should().NotBe(InstallSession.FilesInUseExitCode);
            Directory.Exists(body).Should().BeTrue("the install proceeded");
            probe.CloseCalls.Should().Be(0);
            probe.Waits.Should().HaveCount(FilesInUse.DefaultSettleDelays.Count, "the gate waited the service out first");
            var text = File.ReadAllText(log);
            text.Should().Contain("held by Connected User Experiences and Telemetry [DiagTrack] (system service, pid 4452)");
            text.Should().Contain("continuing");
            text.Should().NotContain("blocked by:");
        }
        finally
        {
            Cleanup(appId);
        }
    }

    [Fact]
    public async Task A_system_holder_that_lets_go_is_not_logged()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Arrange
        using var tmp = new TempDir();
        var log = Path.Combine(tmp.Path, "install.log");
        var appId = "com.acme.r115clear-" + Guid.NewGuid().ToString("N");
        var scans = 0;
        var probe = new FakeProbe(_ => ++scans == 1 ? ServiceOnly() : FilesInUseScan.Clear);
        var blob = InstallBlob(appId, Path.Combine(tmp.Path, "bodydir"));
        var parsed = CommandLineParser.Parse(new[] { "/silent", $"/LOG={log}" }, blob.Parameters);
        var session = InstallSession.ForTesting(blob, parsed);
        session.FilesInUseProbeOverride = probe.Build();

        try
        {
            // Act
            var code = await session.RunHeadlessAsync(new StringWriter(), new StringWriter());

            // Assert
            code.Should().Be(0);
            probe.Waits.Should().ContainSingle();
            File.ReadAllText(log).Should().NotContain("DiagTrack");
        }
        finally
        {
            Cleanup(appId);
        }
    }

    [Fact]
    public async Task A_mixed_result_blocks_a_silent_install_on_the_user_application_alone()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Arrange
        using var tmp = new TempDir();
        var log = Path.Combine(tmp.Path, "install.log");
        var body = Path.Combine(tmp.Path, "bodydir");
        var appId = "com.acme.r115mixed-" + Guid.NewGuid().ToString("N");
        var probe = new FakeProbe(_ => FilesInUse.Classify(Array.Empty<AppBlocker>(), new[] { DiagTrack, Dictum }));
        var blob = InstallBlob(appId, body);
        var parsed = CommandLineParser.Parse(new[] { "/silent", $"/LOG={log}" }, blob.Parameters);
        var session = InstallSession.ForTesting(blob, parsed);
        session.FilesInUseProbeOverride = probe.Build();
        var error = new StringWriter();

        try
        {
            // Act
            var code = await session.RunHeadlessAsync(new StringWriter(), error);

            // Assert
            code.Should().Be(InstallSession.FilesInUseExitCode);
            Directory.Exists(body).Should().BeFalse("a blocked run changes nothing");
            var text = File.ReadAllText(log);
            text.Should().Contain("blocked by: Dictum (pid 9001)");
            text.Should().NotContain("Connected User Experiences and Telemetry", "a service is never named as a blocker");
            probe.Waits.Should().BeEmpty("a user blocker is reported at once");
        }
        finally
        {
            Cleanup(appId);
        }
    }

    [Fact]
    public async Task Closeapps_closes_the_user_application_and_continues_past_the_remaining_service()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Arrange
        using var tmp = new TempDir();
        var log = Path.Combine(tmp.Path, "install.log");
        var body = Path.Combine(tmp.Path, "bodydir");
        var appId = "com.acme.r115close-" + Guid.NewGuid().ToString("N");
        var closed = false;
        var probe = new FakeProbe(
            _ => closed ? ServiceOnly() : FilesInUse.Classify(Array.Empty<AppBlocker>(), new[] { DiagTrack, Dictum }),
            onClose: () => closed = true);
        var blob = InstallBlob(appId, body);
        var parsed = CommandLineParser.Parse(new[] { "/silent", "/closeapps", $"/LOG={log}" }, blob.Parameters);
        var session = InstallSession.ForTesting(blob, parsed);
        session.FilesInUseProbeOverride = probe.Build();

        try
        {
            // Act
            var code = await session.RunHeadlessAsync(new StringWriter(), new StringWriter());

            // Assert
            code.Should().Be(0);
            probe.CloseCalls.Should().Be(1);
            Directory.Exists(body).Should().BeTrue();
            var text = File.ReadAllText(log);
            text.Should().Contain("close-apps: closing 1 blocking application(s)");
            text.Should().Contain("close-apps: all blocking applications closed");
            text.Should().Contain("[DiagTrack]");
        }
        finally
        {
            Cleanup(appId);
        }
    }

    [Fact]
    public async Task Closeapps_asks_nothing_to_close_when_only_a_service_holds_the_directory()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Arrange
        using var tmp = new TempDir();
        var appId = "com.acme.r115closesvc-" + Guid.NewGuid().ToString("N");
        var probe = new FakeProbe(_ => ServiceOnly());
        var blob = InstallBlob(appId, Path.Combine(tmp.Path, "bodydir"));
        var parsed = CommandLineParser.Parse(new[] { "/silent", "/closeapps" }, blob.Parameters);
        var session = InstallSession.ForTesting(blob, parsed);
        session.FilesInUseProbeOverride = probe.Build();

        try
        {
            // Act
            var code = await session.RunHeadlessAsync(new StringWriter(), new StringWriter());

            // Assert
            code.Should().Be(0);
            probe.CloseCalls.Should().Be(0, "a service is never asked to shut down");
        }
        finally
        {
            Cleanup(appId);
        }
    }

    [Fact]
    public async Task A_service_only_result_never_blocks_a_silent_uninstall()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Arrange
        using var tmp = new TempDir();
        var log = Path.Combine(tmp.Path, "u.log");
        var appId = "com.acme.r115uninst-" + Guid.NewGuid().ToString("N");
        UninstallStateStore.Save(appId, new RollbackJournal(), InstallScope.User, Array.Empty<string>());
        var probe = new FakeProbe(_ => ServiceOnly());
        var blob = InstallBlob(appId, body: null);
        var parsed = CommandLineParser.Parse(new[] { "/silent", "/Uninstall", $"/LOG={log}" }, blob.Parameters);
        var session = InstallSession.ForTesting(blob, parsed);
        session.FilesInUseProbeOverride = probe.Build();

        try
        {
            // Act
            var code = await session.RunHeadlessAsync(new StringWriter(), new StringWriter());

            // Assert
            code.Should().Be(0);
            UninstallStateStore.TryLoad(appId, InstallScope.User).Should().BeNull("the uninstall ran");
            File.ReadAllText(log).Should().Contain("[DiagTrack]").And.NotContain("blocked by:");
        }
        finally
        {
            Cleanup(appId);
        }
    }

    [Fact]
    public void The_wizard_probes_list_no_blocker_for_a_service_only_result_so_no_close_apps_step_opens()
    {
        // Arrange
        var appId = "com.acme.r115wiz-" + Guid.NewGuid().ToString("N");
        var probe = new FakeProbe(_ => ServiceOnly());
        var blob = InstallBlob(appId, body: null);
        var session = InstallSession.ForTesting(blob, CommandLineParser.Parse(Array.Empty<string>(), blob.Parameters));
        session.FilesInUseProbeOverride = probe.Build();

        // Act
        var install = session.ScanBlockers(Path.GetTempPath());
        var uninstall = session.ScanUninstallBlockers();

        // Assert
        install.Should().BeEmpty();
        uninstall.Should().BeEmpty();
        probe.Waits.Should().BeEmpty("the wizard's probe does not wait; the gate does");
    }

    [Fact]
    public void The_wizard_probes_list_only_the_user_application_for_a_mixed_result()
    {
        // Arrange
        var appId = "com.acme.r115wizmix-" + Guid.NewGuid().ToString("N");
        var probe = new FakeProbe(_ => FilesInUse.Classify(Array.Empty<AppBlocker>(), new[] { DiagTrack, Dictum, Shell }));
        var blob = InstallBlob(appId, body: null);
        var session = InstallSession.ForTesting(blob, CommandLineParser.Parse(Array.Empty<string>(), blob.Parameters));
        session.FilesInUseProbeOverride = probe.Build();

        // Act
        var install = session.ScanBlockers(Path.GetTempPath());

        // Assert
        install.Should().Equal(new AppBlocker("Dictum", 9001, FromMutex: false));
    }

    [Fact]
    public async Task A_service_only_result_never_blocks_the_interactive_uninstall()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Arrange
        var appId = "com.acme.r115uwiz-" + Guid.NewGuid().ToString("N");
        UninstallStateStore.Save(appId, new RollbackJournal(), InstallScope.User, Array.Empty<string>());
        var probe = new FakeProbe(_ => ServiceOnly());
        var blob = InstallBlob(appId, body: null);
        var session = InstallSession.ForTesting(
            blob, CommandLineParser.Parse(new[] { "/Uninstall" }, blob.Parameters));
        session.FilesInUseProbeOverride = probe.Build();

        try
        {
            // Act
            var outcome = await session.RunUninstallInteractiveAsync(progress: null);

            // Assert
            outcome.IsBlocked.Should().BeFalse();
            outcome.Success.Should().BeTrue(outcome.Error);
        }
        finally
        {
            Cleanup(appId);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static FilesInUseScan ServiceOnly()
        => FilesInUse.Classify(Array.Empty<AppBlocker>(), new[] { DiagTrack });

    private static Func<TimeSpan, CancellationToken, Task> Recorder(List<TimeSpan> waits)
        => (wait, _) =>
        {
            waits.Add(wait);
            return Task.CompletedTask;
        };

    private static WrapperBlob InstallBlob(string appId, string? body)
        => new(
            AppId: appId,
            Parameters: Array.Empty<ParameterDefinition>(),
            InstallSteps: body is null
                ? Array.Empty<InstallStep>()
                : new InstallStep[]
                {
                    // An OS temp directory is never install_dir, so the out-of-tree
                    // write is declared with the production per-step opt-out. Under
                    // test here is the files-in-use gate. (R16)
                    new InstallStep.DirectoryCreate("body", body, When: null, OnFailure.Fail)
                        { AllowOutsideInstallDir = true },
                },
            PreInstall: Array.Empty<InstallStep>(),
            PostInstall: Array.Empty<InstallStep>(),
            UpdateSteps: Array.Empty<InstallStep>());

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

    /// <summary>A fixed Restart Manager result, with every close and settle wait recorded.</summary>
    private sealed class FakeProbe
    {
        private readonly Func<string?, FilesInUseScan> _scan;
        private readonly Action? _onClose;

        public FakeProbe(Func<string?, FilesInUseScan> scan, Action? onClose = null)
        {
            _scan = scan;
            _onClose = onClose;
        }

        public int CloseCalls { get; private set; }

        public List<TimeSpan> Waits { get; } = new();

        public FilesInUseProbe Build() => new()
        {
            Scan = _scan,
            Close = _ =>
            {
                CloseCalls++;
                _onClose?.Invoke();
                return true;
            },
            Delay = Recorder(Waits),
        };
    }
}
