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
/// Files-in-use detection (declared app_mutex + Restart Manager), the silent
/// /closeapps gate, and the setup single-instance lock. Windows-only — the
/// Restart Manager and named mutexes have no cross-platform equivalent. (G7, G17)
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
    /// The gate's positive control: a SEPARATE process holding a file open under the
    /// install directory is still reported. The self-exclusion this file also tests must
    /// not cost the gate its actual job. (R58)
    /// </summary>
    /// <remarks>
    /// The blocker must not be the CURRENT process: asserting that the current process
    /// comes back as a blocker encodes the defect as the contract. It has to be an
    /// independent process for the assertion to mean "the Restart Manager sees somebody
    /// else", so the holder is a child
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
    /// The second positive control, and the reason the exclusion is keyed on the image
    /// PATH rather than on "is an ancestor of mine". The Restart Manager reports a
    /// process whose executable IMAGE merely lives under the scanned directory, even when
    /// it holds no other handle there. That is exactly how the gate catches "the app you
    /// are upgrading is running", and it must keep doing so. (R58)
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

    // ── The running installer is never a blocker of itself (R58) ─────────────

    /// <summary>
    /// The ARP <c>UninstallString</c> is
    /// <c>&lt;install_dir&gt;\uninstall.exe /S /Uninstall /currentuser</c>, so the
    /// dropped uninstaller runs from INSIDE the directory the files-in-use gate sweeps.
    /// Without self-exclusion the Restart Manager reports the uninstaller's own image and
    /// the run is refused with exit 4 — <c>blocked by: installer (pid N)</c>, N being its
    /// own pid — before touching anything. <c>/closeapps</c> cannot rescue it either: the
    /// Restart Manager cannot close its own caller. (R58)
    /// </summary>
    /// <remarks>
    /// The current process here plays the uninstaller: it holds a file open under the
    /// swept directory (<c>FileShare.None</c>, the strongest case) and the sweep must
    /// come back without it. Without the exclusion this goes red with the Restart Manager
    /// reporting the test host by pid — precisely the production symptom.
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
    /// Self-exclusion must leave a genuinely clear directory reading clear, not
    /// "blocked by something with pid 0": with nothing but this process holding the
    /// install dir, the gate has nothing to refuse on. (R58)
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
    /// The elevated-relaunch half. A machine-scope ARP uninstall runs
    /// <c>&lt;install_dir&gt;\uninstall.exe /S /Uninstall /allusers</c> un-elevated;
    /// <c>Elevation.RelaunchElevatedAndWait</c> then relaunches THE SAME IMAGE elevated
    /// and blocks in <c>WaitForSingleObject</c> until the child exits. So while the
    /// elevated child runs its gate, the un-elevated parent is still alive with
    /// <c>&lt;install_dir&gt;\uninstall.exe</c> loaded as its image — which
    /// <see cref="RestartManager_reports_an_app_running_from_inside_the_install_dir"/>
    /// proves the Restart Manager reports. Excluding only the child's own pid leaves
    /// <c>/allusers</c> uninstalls blocked on their own parent. (R58)
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

    /// <summary>The current process is excluded on pid alone, image path or not. (R58)</summary>
    [Fact]
    public void The_current_process_is_always_recognised_as_the_running_installer()
    {
        if (!OperatingSystem.IsWindows()) return;

        FilesInUse.IsRunningInstallerProcess((uint)Environment.ProcessId, selfImagePath: null)
            .Should().BeTrue("self is self regardless of what its image path resolves to");
    }

    /// <summary>
    /// The same-image check must compare the image FILE, not the two path strings.
    /// Production feeds it
    /// <see cref="Environment.ProcessPath"/> (<c>GetModuleFileNameW(NULL)</c>, which
    /// preserves the form the process was LAUNCHED with — 8.3 components, a substituted
    /// drive, a junction) on one side and <c>QueryFullProcessImageNameW(…, 0)</c> (the
    /// canonical long Win32 path) on the other. Under a string comparison those diverge,
    /// the elevated-relaunch parent stops being recognised, and a <c>/allusers</c> ARP
    /// uninstall exits 4 on its own parent again — reachable in the field via
    /// <c>/D=C:\PROGRA~1\Acme</c>. (R58)
    /// </summary>
    /// <remarks>
    /// The process is launched normally and the ALTERNATIVE spelling is supplied as the
    /// running installer's image, which is exactly the production asymmetry
    /// (<c>QueryFullProcessImageNameW</c> canonicalises whatever the launch form was, so
    /// launching through the short path would change nothing observable). The
    /// extended-length spelling carries the assertion on every volume; the 8.3 spelling is
    /// the realistic case but only exists where the volume generates 8.3 aliases, so it is
    /// asserted when available and reported as not demonstrable when not — never silently
    /// skipped into a pass.
    /// </remarks>
    [Fact]
    public void The_same_image_named_a_different_way_is_still_recognised()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var tmp = new TempDir();
        using var app = ImageInDirProcess.Start(tmp.Path, "uninstall.exe");
        try
        {
            var pid = (uint)app.Id;

            FilesInUse.IsRunningInstallerProcess(pid, PathForms.Extended(app.ImagePath))
                .Should().BeTrue(
                    "the extended-length spelling names the same file, so it is the same " +
                    "image — comparing the strings would say otherwise");

            var shortForm = PathForms.Short(app.ImagePath);
            if (shortForm is not null)
            {
                shortForm.Should().NotBe(app.ImagePath, "the short form must actually differ to prove anything");
                FilesInUse.IsRunningInstallerProcess(pid, shortForm).Should().BeTrue(
                    "an 8.3 launch path is what an ARP row written from /D=C:\\PROGRA~1\\… " +
                    "hands back, and it names the same file");
            }
        }
        finally
        {
            app.Kill();
        }
    }

    /// <summary>
    /// The other half: identity must not be confused with the file NAME. A different
    /// file that merely shares the uninstaller's file name, in another directory, is a
    /// stranger and stays a blocker. (R58)
    /// </summary>
    [Fact]
    public void A_different_file_with_the_same_name_is_not_the_installers_image()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var tmp = new TempDir();
        using var other = new TempDir();
        using var app = ImageInDirProcess.Start(tmp.Path, "uninstall.exe");
        try
        {
            // Byte-identical content, identical file name, different file on disk.
            var impostor = Path.Combine(other.Path, "uninstall.exe");
            File.Copy(app.ImagePath, impostor);

            FilesInUse.IsRunningInstallerProcess((uint)app.Id, impostor).Should().BeFalse(
                "same name and same bytes are not the same file — identity is the volume " +
                "serial plus the file id, and this must never widen into a content or " +
                "name match");
        }
        finally
        {
            app.Kill();
        }
    }

    /// <summary>
    /// The same-image branch is reached from <c>Scan</c>, not just from its predicate.
    /// Pinning the predicate alone leaves the wiring between <c>Scan</c> and the
    /// exclusion unguarded — replacing the self-image argument with <c>null</c> stays
    /// green; this asserts both directions through <c>Scan</c>'s own output. (R58)
    /// </summary>
    [Fact]
    public async Task Scan_drops_a_process_running_the_supplied_self_image_and_keeps_it_otherwise()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var tmp = new TempDir();
        using var app = ImageInDirProcess.Start(tmp.Path, "uninstall.exe");
        try
        {
            var pid = (uint)app.Id;

            // With no self image the process is a blocker — which also proves it is
            // genuinely visible to this sweep, so the exclusion below means something.
            var kept = await WaitForBlockerAsync(
                tmp.Path, pid, () => FilesInUse.Scan(null, tmp.Path, selfImagePath: null));
            kept.Should().Contain(b => b.ProcessId == pid, "nothing is excluded without a self image");

            // Same sweep, same process, with that image declared as the running
            // installer's own: gone from the result.
            FilesInUse.Scan(null, tmp.Path, app.ImagePath)
                .Should().NotContain(
                    b => b.ProcessId == pid,
                    "Scan must actually consult the self image it is given — this is the " +
                    "path a /allusers ARP uninstall takes past its own relaunch parent");
        }
        finally
        {
            app.Kill();
        }
    }

    /// <summary>
    /// And the value production supplies to that seam is the running image, so a
    /// mutation to the two-argument overload cannot go unnoticed. (R58)
    /// </summary>
    [Fact]
    public void The_self_image_production_scans_with_is_the_running_process_image()
    {
        FilesInUse.SelfImagePath().Should().Be(
            Environment.ProcessPath,
            "the public Scan overload passes this to the exclusion");
    }

    /// <summary>
    /// The image-path comparison is only as good as the cross-process lookup it rests
    /// on, so that lookup is asserted directly against a child whose path is known. (R58)
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
    private static Task<IReadOnlyList<AppBlocker>> WaitForBlockerAsync(string dir, uint pid)
        => WaitForBlockerAsync(dir, pid, () => FilesInUse.Scan(null, dir));

    /// <summary>
    /// <see cref="WaitForBlockerAsync(string, uint)"/> over an explicit sweep, so a test
    /// can poll the self-image-injecting overload instead of the production one.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task<IReadOnlyList<AppBlocker>> WaitForBlockerAsync(
        string dir, uint pid, Func<IReadOnlyList<AppBlocker>> scan)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        IReadOnlyList<AppBlocker> blockers;
        do
        {
            blockers = scan();
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
                    // An OS temp directory is never install_dir, so the out-of-tree
                    // write is declared with the production per-step opt-out. Under
                    // test here is the files-in-use gate. (R16)
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
                    // An OS temp directory is never install_dir, so the out-of-tree
                    // write is declared with the production per-step opt-out. Under
                    // test here is the files-in-use gate. (R16)
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

    // ── Single-instance lock (G17) ───────────────────────────────────────────

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
    /// The <c>NULL</c>-handle branch must not fail OPEN. <c>CreateMutexW</c> returns
    /// <c>NULL</c> when the name cannot be created; answering that with a non-owning
    /// sentinel indistinguishable from a real lock lets two installs proceed
    /// concurrently, leaving <c>ERROR_ALREADY_EXISTS</c> as the only branch that fails
    /// closed. (R34)
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

    // ── The prior-version uninstaller this installer spawns (R76) ────────────

    /// <summary>
    /// The defect this admits around: a per-user v1 → v2 upgrade holds
    /// <c>Local\sigil-setup-&lt;appId&gt;-user</c> and then runs the PRIOR version's
    /// <c>uninstall.exe /S /Uninstall /currentuser</c> for the teardown. That child
    /// derives the SAME name, so without the handoff it sees
    /// <c>ERROR_ALREADY_EXISTS</c> and exits 5 — every unelevated upgrade and forced
    /// downgrade dies with "removing the previous version failed (uninstaller exit
    /// code 5)" and installs nothing. (R76)
    /// </summary>
    /// <remarks>
    /// <para>
    /// The shape here is the production shape with one honest substitution: the token
    /// names THIS process's real parent (the test host) as the guard holder, while the
    /// lock is actually held by this process. That is precisely what the child sees —
    /// "the token names my parent, and the name is taken" — and it is the strongest
    /// in-process approximation available, because a genuine child would have to be a
    /// separate process. The end-to-end arbiter is
    /// <c>UpgradeInstallTests.Upgrade_replaces_older_version_…</c> in the VM matrix,
    /// which is what actually runs Setup.exe v2 over an installed v1.
    /// </para>
    /// <para>
    /// The negative half of the contract is in the tests below: nothing but a token
    /// naming a live real parent, for this exact guard name, on an uninstall run, is
    /// admitted.
    /// </para>
    /// </remarks>
    [WindowsFact]
    public void The_spawned_prior_uninstaller_is_admitted_under_the_installers_own_lock()
    {
        var appId = "com.acme.p6handoff-" + Guid.NewGuid().ToString("N");
        var name = SetupInstanceLock.NameFor(appId, InstallScope.User);

        // The "installer": holds the guard for the whole teardown.
        using var installer = SetupInstanceLock.TryAcquire(appId, InstallScope.User);
        installer.Should().NotBeNull();

        Environment.SetEnvironmentVariable(SetupInstanceLock.HandoffVariable, RealParentHandoff(name));
        try
        {
            var child = AcquireAsChild(appId, WrapperMode.Uninstall, out var refusal);

            child.Should().NotBeNull(
                "the installer's own teardown child must not be refused as a stranger — " +
                "it runs inside the parent's critical section, which is still exclusive");
            refusal.Should().Be(SetupInstanceLock.SetupLockRefusal.AdmittedByParentInstaller);
            Environment.GetEnvironmentVariable(SetupInstanceLock.HandoffVariable)
                .Should().BeNull("the token is consumed, so nothing further down the tree inherits it");
            child!.Dispose();
        }
        finally
        {
            Environment.SetEnvironmentVariable(SetupInstanceLock.HandoffVariable, null);
        }
    }

    /// <summary>
    /// The guard is unchanged for everyone else: with no handoff a second instance is
    /// refused, uninstall mode included (an ARP uninstall launched by hand while an
    /// install of the same app runs). (R76)
    /// </summary>
    [WindowsFact]
    public void Without_a_handoff_an_uninstall_is_still_refused_while_a_setup_holds_the_guard()
    {
        var appId = "com.acme.p6nohandoff-" + Guid.NewGuid().ToString("N");

        using var installer = SetupInstanceLock.TryAcquire(appId, InstallScope.User);
        installer.Should().NotBeNull();

        var child = AcquireAsChild(appId, WrapperMode.Uninstall, out var refusal);

        child.Should().BeNull();
        refusal.Should().Be(SetupInstanceLock.SetupLockRefusal.AnotherInstanceRunning);
    }

    /// <summary>
    /// The forgery cases, each isolating one binding. A handoff is only ever a claim;
    /// every field of it is checked against something the OS answers. (R76)
    /// </summary>
    [WindowsFact]
    public void A_forged_or_stale_handoff_is_refused()
    {
        var appId = "com.acme.p6forged-" + Guid.NewGuid().ToString("N");
        var name = SetupInstanceLock.NameFor(appId, InstallScope.User);

        SetupInstanceLock.TryGetParentProcessId(out var ppid).Should().BeTrue();
        SetupInstanceLock.TryGetProcessCreationTime(ppid, out var parentCreated).Should().BeTrue();
        var self = (uint)Environment.ProcessId;
        SetupInstanceLock.TryGetProcessCreationTime(self, out var selfCreated).Should().BeTrue();

        // (1) A DIFFERENT app's guard. The binding that stops a planted token from
        //     reaching any install but the one its minter named.
        var otherName = SetupInstanceLock.NameFor(appId + ".other", InstallScope.User);
        SetupInstanceLock.HandoffAdmits(
            SetupInstanceLock.FormatHandoff(ppid, parentCreated, otherName), name)
            .Should().BeFalse("a token minted for another app+scope must not admit this one");

        // (2) A live process that is NOT our parent — here this very process, whose pid
        //     and creation time are both genuine. Proves the pid is read from the OS and
        //     not believed from the token.
        SetupInstanceLock.HandoffAdmits(
            SetupInstanceLock.FormatHandoff(self, selfCreated, name), name)
            .Should().BeFalse("only the process that actually spawned us may hand over its lock");

        // (3) The real parent's pid with the WRONG creation time: the pid-reuse case, a
        //     stale token whose parent has exited and whose number now belongs to
        //     someone else.
        SetupInstanceLock.HandoffAdmits(
            SetupInstanceLock.FormatHandoff(ppid, parentCreated + 1, name), name)
            .Should().BeFalse("the token must name the same process INSTANCE, not just the number");

        // (4) A pid that is genuinely dead. Its creation time was real while it lived.
        var (deadPid, deadCreated) = ShortLivedProcess();
        SetupInstanceLock.HandoffAdmits(
            SetupInstanceLock.FormatHandoff(deadPid, deadCreated, name), name)
            .Should().BeFalse("a dead minter cannot be inside any critical section");

        // (5) Malformed / wrong wire version / empty.
        foreach (var junk in new[]
                 {
                     null, string.Empty, "garbage",
                     $"2|{ppid}|{parentCreated}|{name}",     // unknown version
                     $"1|{ppid}|{parentCreated}",            // truncated
                     $"1|-1|{parentCreated}|{name}",         // not a pid
                     $"1|{ppid}|0|{name}",                   // no creation time
                 })
        {
            SetupInstanceLock.HandoffAdmits(junk, name)
                .Should().BeFalse($"'{junk ?? "<null>"}' is not a well-formed handoff");
        }
    }

    /// <summary>
    /// The payoff ceiling. The handoff is honoured for the uninstall teardown and
    /// nothing else, so no token, however obtained, can put a second CONCURRENT INSTALL
    /// of an application onto the same state. (R76)
    /// </summary>
    [WindowsFact]
    public void A_handoff_never_admits_a_second_install()
    {
        var appId = "com.acme.p6installmode-" + Guid.NewGuid().ToString("N");
        var name = SetupInstanceLock.NameFor(appId, InstallScope.User);

        using var installer = SetupInstanceLock.TryAcquire(appId, InstallScope.User);
        installer.Should().NotBeNull();

        Environment.SetEnvironmentVariable(SetupInstanceLock.HandoffVariable, RealParentHandoff(name));
        try
        {
            var second = AcquireAsChild(appId, WrapperMode.Install, out var refusal);

            second.Should().BeNull("an install is never the teardown child");
            refusal.Should().Be(SetupInstanceLock.SetupLockRefusal.AnotherInstanceRunning);
            Environment.GetEnvironmentVariable(SetupInstanceLock.HandoffVariable)
                .Should().BeNull("the token is consumed on every path, admitted or not");
        }
        finally
        {
            Environment.SetEnvironmentVariable(SetupInstanceLock.HandoffVariable, null);
        }
    }

    /// <summary>
    /// The handoff must not reopen the squatted-name hole: when the guard's name is
    /// occupied by something that is not our mutex, no exclusivity was ever established,
    /// so there is no critical section to be admitted into. A valid handoff does not
    /// rescue that branch. (R34, R76)
    /// </summary>
    [WindowsFact]
    public void A_squatted_guard_name_is_not_rescued_by_a_valid_handoff()
    {
        var appId = "com.acme.p6squathandoff-" + Guid.NewGuid().ToString("N");
        var name = SetupInstanceLock.NameFor(appId, InstallScope.User);

        using var squatter = new Semaphore(1, 1, name, out var createdNew);
        createdNew.Should().BeTrue();

        Environment.SetEnvironmentVariable(SetupInstanceLock.HandoffVariable, RealParentHandoff(name));
        try
        {
            var child = AcquireAsChild(appId, WrapperMode.Uninstall, out var refusal);

            child.Should().BeNull("R34's fail-closed branch stays closed");
            refusal.Should().Be(SetupInstanceLock.SetupLockRefusal.NameNotAvailable);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SetupInstanceLock.HandoffVariable, null);
        }
    }

    /// <summary>
    /// The admission is one level deep. An admitted child does not own the guard, so it
    /// cannot mint a handoff of its own: no chain of processes can walk the exemption
    /// outwards. (R76)
    /// </summary>
    [WindowsFact]
    public void An_admitted_child_cannot_mint_a_further_handoff()
    {
        var appId = "com.acme.p6chain-" + Guid.NewGuid().ToString("N");
        var name = SetupInstanceLock.NameFor(appId, InstallScope.User);

        using var installer = SetupInstanceLock.TryAcquire(appId, InstallScope.User);
        installer!.MintChildHandoff().Should().NotBeNull("the owner is the one that may hand over");

        Environment.SetEnvironmentVariable(SetupInstanceLock.HandoffVariable, RealParentHandoff(name));
        try
        {
            using var child = AcquireAsChild(appId, WrapperMode.Uninstall, out _);

            child.Should().NotBeNull();
            child!.OwnsTheGuard.Should().BeFalse();
            child.MintChildHandoff().Should().BeNull("only the guard's owner may hand it over");
        }
        finally
        {
            Environment.SetEnvironmentVariable(SetupInstanceLock.HandoffVariable, null);
        }
    }

    /// <summary>
    /// The guard name carries the scope, so a token minted for one scope must not admit
    /// the other. This is what makes "machine scope is the same code with a
    /// <c>Global\</c> name" a tested claim rather than an inspected one: the elevated
    /// shape reaches the identical check, and a per-user token cannot cross into it. (R76)
    /// </summary>
    /// <remarks>
    /// Pure verification — no mutex is created, so the machine-scope half needs no
    /// elevation and no <c>Global\</c> name (creating one needs
    /// <c>SeCreateGlobalPrivilege</c>; deriving one needs nothing). Both directions are
    /// asserted, each against its own positive control, so a refusal cannot be passing
    /// for the wrong reason.
    /// </remarks>
    [WindowsFact]
    public void A_handoff_is_bound_to_the_scope_it_was_minted_for()
    {
        var appId = "com.acme.p6scope-" + Guid.NewGuid().ToString("N");
        var userName = SetupInstanceLock.NameFor(appId, InstallScope.User);
        var machineName = SetupInstanceLock.NameFor(appId, InstallScope.Machine);
        machineName.Should().StartWith("Global\\").And.NotBe(userName);

        SetupInstanceLock.TryGetParentProcessId(out var ppid).Should().BeTrue();
        SetupInstanceLock.TryGetProcessCreationTime(ppid, out var created).Should().BeTrue();

        var userToken = SetupInstanceLock.FormatHandoff(ppid, created, userName);
        var machineToken = SetupInstanceLock.FormatHandoff(ppid, created, machineName);

        SetupInstanceLock.HandoffAdmits(userToken, userName)
            .Should().BeTrue("control: the user-scope token admits its own guard");
        SetupInstanceLock.HandoffAdmits(machineToken, machineName)
            .Should().BeTrue("control: the machine-scope token admits its own guard");

        SetupInstanceLock.HandoffAdmits(userToken, machineName)
            .Should().BeFalse("a per-user handoff must not admit an elevated machine-scope run");
        SetupInstanceLock.HandoffAdmits(machineToken, userName)
            .Should().BeFalse("a machine-scope handoff must not admit a per-user run");
    }

    /// <summary>
    /// What a spawned child actually does: consume the token its parent left on the
    /// environment, then take the guard with it. The consume happens at the top of
    /// <c>Main</c> in production (before the elevation branch); the ordering relative to
    /// the acquisition is what these tests reproduce.
    /// </summary>
    private static SetupInstanceLock? AcquireAsChild(
        string appId, WrapperMode mode, out SetupInstanceLock.SetupLockRefusal refusal)
        => SetupInstanceLock.TryAcquire(
            appId, InstallScope.User, mode, SetupInstanceLock.ConsumeHandoffToken(), out refusal);

    /// <summary>
    /// A handoff naming this process's REAL parent — the shape a spawned child sees,
    /// with the test standing in for the installer that holds the lock.
    /// </summary>
    private static string RealParentHandoff(string lockName)
    {
        SetupInstanceLock.TryGetParentProcessId(out var ppid)
            .Should().BeTrue("the parent pid is what the handoff is checked against");
        SetupInstanceLock.TryGetProcessCreationTime(ppid, out var created)
            .Should().BeTrue("the parent must be live and readable");
        return SetupInstanceLock.FormatHandoff(ppid, created, lockName);
    }

    /// <summary>
    /// A pid whose process has exited, with the creation time it really had while it
    /// lived. The <see cref="Process"/> handle is released before returning, so the
    /// kernel object goes away with it.
    /// </summary>
    private static (uint Pid, long CreationTime) ShortLivedProcess()
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            Arguments = "/c exit 0",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi);
        p.Should().NotBeNull();
        var pid = (uint)p!.Id;
        SetupInstanceLock.TryGetProcessCreationTime(pid, out var created).Should().BeTrue();
        p.WaitForExit();
        p.Dispose();
        return (pid, created);
    }
}
