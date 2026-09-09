using System;
using System.Collections.Generic;
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

    /// <summary>
    /// R58 — the gate's positive control: a SEPARATE process holding a file open under
    /// the install directory is still reported. The self-exclusion this file also tests
    /// must not cost the gate its actual job.
    /// </summary>
    /// <remarks>
    /// This test used to hold the file from the CURRENT process and assert that the
    /// current process came back as a blocker — it encoded the R58 defect as the
    /// contract. The blocker has to be an independent process for the assertion to mean
    /// "the Restart Manager sees somebody else", so the holder is a child
    /// <c>powershell.exe</c>: its own image lives in System32, well outside the scanned
    /// directory, so the only thing tying it to the sweep is the open handle.
    /// </remarks>
    [Fact]
    public async Task RestartManager_reports_another_process_holding_a_file_in_the_install_dir()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var tmp = new TempDir();
        using var flags = new TempDir(); // the ready flag lives OUTSIDE the scanned dir
        var target = Path.Combine(tmp.Path, "locked.txt");
        File.WriteAllText(target, "payload");

        using var holder = FileHolderProcess.Start(target, Path.Combine(flags.Path, "ready"));
        try
        {
            var blockers = await WaitForBlockerAsync(tmp.Path, (uint)holder.Id);

            blockers.Should().Contain(
                b => b.ProcessId == (uint)holder.Id,
                "the Restart Manager must still report an unrelated process holding a file");
            blockers.Select(b => b.Describe()).Should()
                .Contain(d => d.Contains("pid", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            holder.Kill();
        }
    }

    /// <summary>
    /// R58 — the second positive control, and the reason the exclusion is keyed on the
    /// image PATH rather than on "is an ancestor of mine". The Restart Manager reports a
    /// process whose executable IMAGE merely lives under the scanned directory, even when
    /// it holds no other handle there. That is exactly how the gate catches "the app you
    /// are upgrading is running", and it must keep doing so.
    /// </summary>
    [Fact]
    public async Task RestartManager_reports_an_app_running_from_inside_the_install_dir()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var tmp = new TempDir();
        using var app = ImageInDirProcess.Start(tmp.Path, "the-app.exe");
        try
        {
            var blockers = await WaitForBlockerAsync(tmp.Path, (uint)app.Id);

            blockers.Should().Contain(
                b => b.ProcessId == (uint)app.Id,
                "a running app whose image sits in the install dir is a genuine blocker — " +
                "the Restart Manager reports it for the loaded image alone, with no data " +
                "file open, and the gate must keep refusing on it");
        }
        finally
        {
            app.Kill();
        }
    }

    // ── R58: the running installer is never a blocker of itself ──────────────

    /// <summary>
    /// R58 (release blocker, found at G2 check 1) — the ARP <c>UninstallString</c> is
    /// <c>&lt;install_dir&gt;\uninstall.exe /S /Uninstall /currentuser</c>, so T15's
    /// dropped uninstaller runs from INSIDE the directory the P6 gate sweeps. With no
    /// self-exclusion the Restart Manager reported the uninstaller's own image and the
    /// run was refused with exit 4 — <c>blocked by: installer (pid N)</c>, N being its
    /// own pid — before touching anything. <c>/closeapps</c> could not rescue it either:
    /// the Restart Manager cannot close its own caller.
    /// </summary>
    /// <remarks>
    /// The current process here plays the uninstaller: it holds a file open under the
    /// swept directory (<c>FileShare.None</c>, the strongest case) and the sweep must
    /// come back without it. Before the fix this failed, the Restart Manager reporting
    /// the test host by pid — precisely the production symptom.
    /// </remarks>
    [Fact]
    public async Task Scan_never_reports_the_running_process_as_its_own_blocker()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var tmp = new TempDir();
        var target = Path.Combine(tmp.Path, "uninstall.exe");
        File.WriteAllText(target, "stand-in for the running uninstaller image");

        using (new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var blockers = FilesInUse.Scan(null, tmp.Path);

            blockers.Should().NotContain(
                b => b.ProcessId == (uint)Environment.ProcessId,
                "the process running the install/uninstall can never be a blocker of " +
                "itself — that is what made the ARP UninstallString exit 4 with its own pid");
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// R58 — self-exclusion must leave a genuinely clear directory reading clear, not
    /// "blocked by something with pid 0": with nothing but this process holding the
    /// install dir, the gate has nothing to refuse on.
    /// </summary>
    [Fact]
    public void Scan_is_clear_when_only_the_running_process_holds_the_install_dir()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var tmp = new TempDir();
        var target = Path.Combine(tmp.Path, "app.dat");
        File.WriteAllText(target, "payload");

        using var hold = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        FilesInUse.Scan(null, tmp.Path).Should().BeEmpty(
            "nothing but this process holds the directory, so the gate is clear");
    }

    /// <summary>
    /// R58 — the T12 half. A machine-scope ARP uninstall runs
    /// <c>&lt;install_dir&gt;\uninstall.exe /S /Uninstall /allusers</c> un-elevated;
    /// <c>Elevation.RelaunchElevatedAndWait</c> then relaunches THE SAME IMAGE elevated
    /// and blocks in <c>WaitForSingleObject</c> until the child exits. So while the
    /// elevated child runs its gate, the un-elevated parent is still alive with
    /// <c>&lt;install_dir&gt;\uninstall.exe</c> loaded as its image — which
    /// <see cref="RestartManager_reports_an_app_running_from_inside_the_install_dir"/>
    /// proves the Restart Manager reports. Excluding only the child's own pid would have
    /// left <c>/allusers</c> uninstalls blocked on their own parent.
    /// </summary>
    /// <remarks>
    /// The relaunch pair is simulated rather than elevated: a child process is started
    /// from a known image path and that same path is handed in as "the running
    /// installer's image", which is the relationship <c>ShellExecuteExW</c>'s
    /// <c>runas</c> verb creates. The negative halves are the load-bearing ones — a
    /// different image is not excluded, and neither is anything when the running image is
    /// unknown.
    /// </remarks>
    [Fact]
    public void A_process_running_the_installers_own_image_is_excluded_but_no_other_is()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var tmp = new TempDir();
        using var app = ImageInDirProcess.Start(tmp.Path, "uninstall.exe");
        try
        {
            var pid = (uint)app.Id;

            FilesInUse.IsRunningInstallerProcess(pid, app.ImagePath).Should().BeTrue(
                "a process running the very image this installer runs from is the T12 " +
                "un-elevated relaunch parent (or this process), never a third-party app");

            FilesInUse.IsRunningInstallerProcess(pid, Path.Combine(tmp.Path, "something-else.exe"))
                .Should().BeFalse("a different image is a real blocker and must survive the sweep");

            FilesInUse.IsRunningInstallerProcess(pid, null)
                .Should().BeFalse("with no known self image there is nothing to match on");
        }
        finally
        {
            app.Kill();
        }
    }

    /// <summary>R58 — the current process is excluded on pid alone, image path or not.</summary>
    [Fact]
    public void The_current_process_is_always_recognised_as_the_running_installer()
    {
        if (!OperatingSystem.IsWindows()) return;

        FilesInUse.IsRunningInstallerProcess((uint)Environment.ProcessId, selfImagePath: null)
            .Should().BeTrue("self is self regardless of what its image path resolves to");
    }

    /// <summary>
    /// R58 — the image-path comparison is only as good as the cross-process lookup it
    /// rests on, so that lookup is asserted directly against a child whose path is known.
    /// </summary>
    [Fact]
    public void The_image_path_of_another_process_can_be_read()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var tmp = new TempDir();
        using var app = ImageInDirProcess.Start(tmp.Path, "probe.exe");
        try
        {
            // BeEquivalentTo, not Be: Windows paths are case-insensitive, and so is the
            // comparison the sweep itself makes.
            FilesInUse.TryGetProcessImagePath((uint)app.Id)
                .Should().BeEquivalentTo(app.ImagePath,
                    "the sweep resolves a blocker's image in order to compare it with its own");
        }
        finally
        {
            app.Kill();
        }
    }

    /// <summary>
    /// Poll the sweep until <paramref name="pid"/> shows up, so a child that is still
    /// starting cannot make the positive controls flaky. Returns the last sweep either
    /// way; the caller asserts, so a timeout surfaces as an ordinary assertion failure
    /// rather than a hang.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task<IReadOnlyList<AppBlocker>> WaitForBlockerAsync(string dir, uint pid)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        IReadOnlyList<AppBlocker> blockers;
        do
        {
            blockers = FilesInUse.Scan(null, dir);
            if (blockers.Any(b => b.ProcessId == pid))
            {
                return blockers;
            }
            await Task.Delay(250).ConfigureAwait(false);
        }
        while (DateTime.UtcNow < deadline);
        return blockers;
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
