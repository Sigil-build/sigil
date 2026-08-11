using System;
using System.Diagnostics;
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
/// P6 (gaps G7/G17): files-in-use detection (declared app_mutex + Restart Manager),
/// the silent /closeapps gate, and the setup single-instance lock. Windows-only —
/// the Restart Manager and named mutexes have no cross-platform equivalent.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class FilesInUseTests
{
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

    // ── Declared app_mutex probe ─────────────────────────────────────────────

    [Fact]
    public void Scan_reports_a_held_app_mutex_and_ignores_an_unheld_one()
    {
        if (!OperatingSystem.IsWindows()) return;

        var name = "Local\\sigil-test-mutex-" + Guid.NewGuid().ToString("N");

        // Not held yet → clear.
        FilesInUse.Scan(new[] { name }, installDir: null).Should().BeEmpty();

        using (var held = new Mutex(initiallyOwned: true, name))
        {
            var blockers = FilesInUse.Scan(new[] { name }, installDir: null);
            blockers.Should().ContainSingle();
            blockers[0].FromMutex.Should().BeTrue();
            blockers[0].Name.Should().Be(name);
            blockers[0].Describe().Should().Contain("mutex");
        }

        // Released → clear again.
        FilesInUse.Scan(new[] { name }, installDir: null).Should().BeEmpty();
    }

    [Fact]
    public void Scan_is_clear_for_no_mutexes_and_a_nonexistent_dir()
    {
        FilesInUse.Scan(null, null).Should().BeEmpty();
        FilesInUse.Scan(Array.Empty<string>(), Path.Combine(Path.GetTempPath(), "sigil-nope-" + Guid.NewGuid().ToString("N")))
            .Should().BeEmpty();
    }

    // ── Restart Manager sweep ────────────────────────────────────────────────

    [Fact]
    public async Task RestartManager_reports_a_process_holding_a_file_in_the_install_dir()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var tmp = new TempDir();
        var target = Path.Combine(tmp.Path, "locked.txt");
        File.WriteAllText(target, "payload");

        // Hold the file open from THIS process — the Restart Manager reports the
        // current process as a blocker just as it would any other app.
        using (var hold = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var blockers = FilesInUse.Scan(null, tmp.Path);

            blockers.Should().NotBeEmpty("the Restart Manager should see this process holding the file");
            blockers.Should().Contain(b => b.ProcessId == (uint)Environment.ProcessId);
            blockers.Select(b => b.Describe()).Should().Contain(d => d.Contains("pid", StringComparison.OrdinalIgnoreCase));
        }

        await Task.CompletedTask;
    }

    // ── Silent gate: exit 4 without /closeapps ───────────────────────────────

    [Fact]
    public async Task Silent_install_blocked_by_a_held_mutex_exits_with_the_files_in_use_code()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var tmp = new TempDir();
        var log = Path.Combine(tmp.Path, "blocked.log");
        var mutexName = "Local\\sigil-test-block-" + Guid.NewGuid().ToString("N");
        var appId = "com.acme.p6block-" + Guid.NewGuid().ToString("N");
        var body = Path.Combine(tmp.Path, "bodydir");

        using var held = new Mutex(initiallyOwned: true, mutexName);
        try
        {
            var blob = new WrapperBlob(
                AppId: appId,
                Parameters: Array.Empty<ParameterDefinition>(),
                InstallSteps: new InstallStep[]
                {
                    // R16: an OS temp directory is never install_dir, so the
                    // out-of-tree write is declared with the production per-step
                    // opt-out. Under test here is the files-in-use gate.
                    new InstallStep.DirectoryCreate("body", body, When: null, OnFailure.Fail)
                        { AllowOutsideInstallDir = true },
                },
                PreInstall: Array.Empty<InstallStep>(),
                PostInstall: Array.Empty<InstallStep>(),
                UpdateSteps: Array.Empty<InstallStep>(),
                AppMutex: new[] { mutexName });

            var parsed = CommandLineParser.Parse(new[] { "/silent", $"/LOG={log}" }, blob.Parameters);
            var session = InstallSession.ForTesting(blob, parsed);

            var code = await session.RunHeadlessAsync(new StringWriter(), new StringWriter());

            code.Should().Be(InstallSession.FilesInUseExitCode);
            Directory.Exists(body).Should().BeFalse("a blocked run must change nothing — the journal never opens");

            var logText = File.ReadAllText(log);
            logText.Should().Contain(mutexName, "the log names the blocker");
            logText.Should().Contain($"exit code: {InstallSession.FilesInUseExitCode}");
        }
        finally
        {
            Cleanup(appId);
        }
    }

    [Fact]
    public async Task Silent_install_is_not_blocked_when_no_mutex_is_held()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var tmp = new TempDir();
        var appId = "com.acme.p6clear-" + Guid.NewGuid().ToString("N");
        var body = Path.Combine(tmp.Path, "bodydir");
        var mutexName = "Local\\sigil-test-free-" + Guid.NewGuid().ToString("N");

        try
        {
            var blob = new WrapperBlob(
                AppId: appId,
                Parameters: Array.Empty<ParameterDefinition>(),
                InstallSteps: new InstallStep[]
                {
                    // R16: an OS temp directory is never install_dir, so the
                    // out-of-tree write is declared with the production per-step
                    // opt-out. Under test here is the files-in-use gate.
                    new InstallStep.DirectoryCreate("body", body, When: null, OnFailure.Fail)
                        { AllowOutsideInstallDir = true },
                },
                PreInstall: Array.Empty<InstallStep>(),
                PostInstall: Array.Empty<InstallStep>(),
                UpdateSteps: Array.Empty<InstallStep>(),
                AppMutex: new[] { mutexName }); // declared but nobody holds it

            var parsed = CommandLineParser.Parse(new[] { "/silent" }, blob.Parameters);
            var session = InstallSession.ForTesting(blob, parsed);

            (await session.RunHeadlessAsync(new StringWriter(), new StringWriter())).Should().Be(0);
            Directory.Exists(body).Should().BeTrue();
        }
        finally
        {
            Cleanup(appId);
        }
    }

    [Fact]
    public async Task Closeapps_cannot_clear_a_held_mutex_so_the_run_is_still_refused()
    {
        if (!OperatingSystem.IsWindows()) return;

        // /closeapps asks the Restart Manager to close processes holding FILES; a bare
        // declared mutex has no file registration and no process handle, so it cannot
        // be closed that way — the run must still refuse rather than proceed blindly.
        using var tmp = new TempDir();
        var mutexName = "Local\\sigil-test-stubborn-" + Guid.NewGuid().ToString("N");
        var appId = "com.acme.p6stubborn-" + Guid.NewGuid().ToString("N");

        using var held = new Mutex(initiallyOwned: true, mutexName);
        try
        {
            var blob = new WrapperBlob(
                AppId: appId,
                Parameters: Array.Empty<ParameterDefinition>(),
                InstallSteps: Array.Empty<InstallStep>(),
                PreInstall: Array.Empty<InstallStep>(),
                PostInstall: Array.Empty<InstallStep>(),
                UpdateSteps: Array.Empty<InstallStep>(),
                AppMutex: new[] { mutexName });

            var parsed = CommandLineParser.Parse(new[] { "/silent", "/closeapps" }, blob.Parameters);
            var session = InstallSession.ForTesting(blob, parsed);

            (await session.RunHeadlessAsync(new StringWriter(), new StringWriter()))
                .Should().Be(InstallSession.FilesInUseExitCode);
        }
        finally
        {
            Cleanup(appId);
        }
    }

    // ── Uninstall inherits the gate ──────────────────────────────────────────

    [Fact]
    public async Task Uninstall_is_blocked_by_a_held_mutex()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var tmp = new TempDir();
        var log = Path.Combine(tmp.Path, "u.log");
        var mutexName = "Local\\sigil-test-udep-" + Guid.NewGuid().ToString("N");
        var appId = "com.acme.p6uninst-" + Guid.NewGuid().ToString("N");

        UninstallStateStore.Save(appId, new RollbackJournal(), InstallScope.User, Array.Empty<string>());
        using var held = new Mutex(initiallyOwned: true, mutexName);
        try
        {
            var blob = new WrapperBlob(
                AppId: appId,
                Parameters: Array.Empty<ParameterDefinition>(),
                InstallSteps: Array.Empty<InstallStep>(),
                PreInstall: Array.Empty<InstallStep>(),
                PostInstall: Array.Empty<InstallStep>(),
                UpdateSteps: Array.Empty<InstallStep>(),
                AppMutex: new[] { mutexName });

            var parsed = CommandLineParser.Parse(new[] { "/silent", "/Uninstall", $"/LOG={log}" }, blob.Parameters);
            var session = InstallSession.ForTesting(blob, parsed);

            var code = await session.RunHeadlessAsync(new StringWriter(), new StringWriter());

            code.Should().Be(InstallSession.FilesInUseExitCode, "uninstall.exe inherits the same parser and gate");
            UninstallStateStore.TryLoad(appId, InstallScope.User)
                .Should().NotBeNull("a blocked uninstall must not have removed the recorded state");
            File.ReadAllText(log).Should().Contain(mutexName);
        }
        finally
        {
            Cleanup(appId);
        }
    }

    // ── Single-instance lock (gap G17) ───────────────────────────────────────

    [Fact]
    public void Second_setup_instance_is_refused_and_the_first_is_unaffected()
    {
        if (!OperatingSystem.IsWindows()) return;

        var appId = "com.acme.p6instance-" + Guid.NewGuid().ToString("N");

        using var first = SetupInstanceLock.TryAcquire(appId, InstallScope.User);
        first.Should().NotBeNull("the first instance owns the install");

        var second = SetupInstanceLock.TryAcquire(appId, InstallScope.User);
        second.Should().BeNull("a second simultaneous instance is refused");

        // Releasing the first frees the name for a later run.
        first!.Dispose();
        using var third = SetupInstanceLock.TryAcquire(appId, InstallScope.User);
        third.Should().NotBeNull("the name is free once the first instance exits");
    }

    [Fact]
    public void Instance_lock_name_is_scoped_by_app_and_scope()
    {
        var user = SetupInstanceLock.NameFor("com.acme.App", InstallScope.User);
        var machine = SetupInstanceLock.NameFor("com.acme.App", InstallScope.Machine);

        user.Should().StartWith("Local\\").And.Contain("com.acme.App").And.EndWith("user");
        machine.Should().StartWith("Global\\", "a machine install must be exclusive across sessions").And.EndWith("machine");
        user.Should().NotBe(machine, "the two scopes install independently");
    }

    [Fact]
    public void Different_apps_do_not_block_each_other()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var a = SetupInstanceLock.TryAcquire("com.acme.AppA-" + Guid.NewGuid().ToString("N"), InstallScope.User);
        using var b = SetupInstanceLock.TryAcquire("com.acme.AppB-" + Guid.NewGuid().ToString("N"), InstallScope.User);
        a.Should().NotBeNull();
        b.Should().NotBeNull("a different app's setup is independent");
    }

    /// <summary>
    /// R34 — the <c>NULL</c>-handle branch used to fail OPEN. <c>CreateMutexW</c> returns
    /// <c>NULL</c> when the name cannot be created, and the code answered with a
    /// non-owning sentinel indistinguishable from a real lock, so two installs could
    /// proceed concurrently. <c>ERROR_ALREADY_EXISTS</c> was the only branch that failed
    /// closed.
    /// </summary>
    /// <remarks>
    /// The squat is reproduced the cheap way, which is also the realistic way: the mutex
    /// name is fully derivable from the public app id
    /// (<see cref="SetupInstanceLock.NameFor"/>), and creating a DIFFERENT kind of kernel
    /// object under it — here a semaphore — makes every later <c>CreateMutexW</c> on that
    /// name fail with a <c>NULL</c> handle. Same user, same session, no privilege
    /// required, and nothing survives the test: the semaphore dies with its handle.
    /// </remarks>
    [Fact]
    public void A_squatted_guard_name_fails_closed_rather_than_pretending_to_hold_a_lock()
    {
        if (!OperatingSystem.IsWindows()) return;

        var appId = "com.acme.p6squat-" + Guid.NewGuid().ToString("N");
        var name = SetupInstanceLock.NameFor(appId, InstallScope.User);

        // Occupy the guard's name with an object that is not a mutex.
        using var squatter = new System.Threading.Semaphore(1, 1, name, out var createdNew);
        createdNew.Should().BeTrue("the test must own the squat for this to prove anything");

        var taken = SetupInstanceLock.TryAcquire(appId, InstallScope.User);

        taken.Should().BeNull(
            "the guard's name is occupied, so no exclusivity was established — answering " +
            "with a non-owning sentinel lets a second setup run concurrently while both " +
            "believe they hold the lock");
    }
}
