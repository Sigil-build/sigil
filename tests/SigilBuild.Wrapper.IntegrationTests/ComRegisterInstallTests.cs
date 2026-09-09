namespace SigilBuild.Wrapper.IntegrationTests;

using System;
using System.IO;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Steps;
using SigilBuild.Wrapper.Steps.Win32;
using Xunit;

/// <summary>
/// T13.1 (P13): the live leg for <c>com_register</c> (P11 / T11.2), deferred
/// to CI-VM when that step shipped with unit/parse/roundtrip coverage only.
/// </summary>
/// <remarks>
/// <para>
/// <b>The pragmatic call (see the T13.1 report for the full writeup):</b> a
/// genuine "register → assert <c>HKCR\CLSID\{..}</c> appears → unregister →
/// assert gone" leg needs a real self-registering COM DLL. No such fixture
/// exists in this repo, and none was added here — deliberately. The
/// candidate alternative (self-registering system DLLs already on every
/// Windows image, e.g. <c>actxprxy.dll</c>) was rejected: those CLSIDs are
/// already registered by the OS before this test ever runs, so "register →
/// assert present" would prove nothing (the key was already there), and
/// unregistering a real system COM DLL on a shared CI runner — even one
/// that's supposed to leave no trace — is exactly the "fragile fixture" the
/// brief says not to invent. <see cref="Live_register_then_unregister_a_real_self_registering_dll"/>
/// below is therefore an explicit <c>xunit</c> <c>Skip</c> (reported
/// "Skipped" in every environment, not just gated by OS/elevation) that
/// documents the follow-up: bundle a tiny, purpose-built self-registering
/// test DLL (its own <c>DllRegisterServer</c>/<c>DllUnregisterServer</c>
/// writing under a private, disposable CLSID) and swap this Skip for a real
/// body.
/// </para>
/// <para>
/// What CAN be verified live, and is verified below in
/// <see cref="ComRegisterStep_runs_the_full_register_journal_reverse_plumbing_under_elevation"/>:
/// the step's real end-to-end plumbing — resolve path, clear the privileged-target
/// anchor, journal the inverse BEFORE the native call, invoke
/// <c>LoadLibraryEx</c>/<c>GetProcAddress</c> through the AOT-safe function
/// pointer, map the outcome to a <see cref="StepResult"/>, and run the journaled
/// <see cref="RollbackRecord.UnregisterCom"/> undo — genuinely elevated, on
/// the CI VM, rather than only unit-tested unelevated (as
/// <c>ComRegisterStepTests</c> already does locally). The DLL it names has no
/// <c>DllRegisterServer</c> export, so it never touches real HKCR state, while
/// still proving elevation doesn't change the step's failure-path behavior.
/// </para>
/// <para>
/// <b>The DLL now lives inside a real <c>install_dir</c> (register row R67).</b>
/// As written in P11 this leg ran against <see cref="StepContext.Empty"/> and
/// named <c>%SystemRoot%\System32\kernel32.dll</c> — "present on every Windows
/// host". Stage 1 lane S2 (rows R3/R9/R16) then anchored every SYSTEM-level step
/// target to the run's resolved <c>install_dir</c>, and the matrix's first real
/// run refused the step exactly as designed — <c>com_register</c> is the sharpest
/// of the four targets, since the DLL is loaded into the <em>elevated installer
/// process</em>. So the harness now resolves a genuine machine-scope
/// <c>install_dir</c> (<see cref="SystemStepInstallDir"/>) and <b>copies</b> the
/// DLL into it, which is the <c>file_copy</c>-then-<c>com_register</c> ordering
/// <c>docs/guides/install-steps.md</c> prescribes. Every assertion below is the
/// one P11 wrote; only the anchor and the DLL's location changed.
/// </para>
/// <para>
/// <b>Why the copy is <c>winmm.dll</c> under a unique name, not
/// <c>kernel32.dll</c>.</b> <c>kernel32.dll</c> is a <c>KnownDLLs</c> entry, so
/// <c>LoadLibraryEx</c> on a copy of it is satisfied by the already-mapped system
/// module regardless of the path passed — the leg would report success while
/// having proved nothing about the file inside <c>install_dir</c>.
/// <c>winmm.dll</c> ships in <c>System32</c> on every Windows host, is not a
/// <c>KnownDLLs</c> entry, loads self-standing from an arbitrary directory (the
/// same probe <c>NativeRuntimeBootstrapTests</c> uses for its DLL-search leg), and
/// exports no <c>DllRegisterServer</c> — so the load genuinely comes from
/// <c>install_dir</c> and the outcome is genuinely <c>ExportMissing</c>. The
/// unique base name keeps any already-loaded module of the same name from
/// short-circuiting the loader.
/// </para>
/// <para>
/// <b>Gating:</b> reports a genuine Skipped result (via
/// <see cref="VmSystemStepsFactAttribute"/>, register row R6 — the same convention
/// as <c>PrerequisiteInstallTests</c>/<c>UpgradeInstallTests</c>) unless the
/// host is Windows, <c>SIGIL_VM_TESTS=1</c> and <c>SIGIL_VM_SYSTEMSTEPS=1</c>
/// are both set, AND the current process is elevated
/// (<see cref="Elevation.IsProcessElevated"/>). This is NOT run locally in
/// this sandbox (not Windows, not elevated, env vars unset) — the CI VM job
/// (<c>p11-system-steps-vm</c> in <c>wrapper-vm-tests.yml</c>) sets all three
/// and runs on a real elevated <c>windows-latest</c> runner.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public class ComRegisterInstallTests
{
    [VmSystemStepsFact]
    public async Task ComRegisterStep_runs_the_full_register_journal_reverse_plumbing_under_elevation()
    {
        using var installDir = SystemStepInstallDir.CreateElevated();

        // The "COM DLL this installer shipped": a copy of winmm.dll inside
        // install_dir, under a unique base name. It loads for real (so the
        // LoadLibraryEx / GetProcAddress plumbing is genuinely exercised) but has
        // no DllRegisterServer export — so it never touches real HKCR/CLSID state,
        // even under elevation.
        var dll = installDir.CopyIn(
            Path.Combine(Environment.SystemDirectory, "winmm.dll"),
            $"sigilcomprobe_{Guid.NewGuid():N}.dll");

        var spec = new InstallStep.ComRegister("it-comreg", dll, When: null, OnFailure: OnFailure.Continue);
        var journal = new RollbackJournal();

        var result = await new ComRegisterStep(spec)
            .RunAsync(installDir.Context, journal, default);

        result.Success.Should().BeFalse("the probe DLL has no DllRegisterServer export");
        result.Error.Should().Contain("self-registering COM DLL");
        result.Error.Should().NotContain("LoadLibraryEx",
            "the DLL must have loaded for real from install_dir — a load failure would mean this " +
            "leg never reached GetProcAddress and so proved nothing about the plumbing");

        journal.Records.Should().ContainSingle()
            .Which.Should().BeOfType<RollbackRecord.UnregisterCom>()
            .Which.DllPath.Should().Be(dll);

        // The undo is best-effort and must not throw even when there was
        // nothing to unregister — the same tolerance RemoveService and
        // DeleteFirewallRule's undo apply to a target that was never created.
        var undo = () => journal.Records[0].UndoAsync(default);
        await undo.Should().NotThrowAsync();
    }

    [Fact(Skip =
        "com_register's live register->assert HKCR\\CLSID->unregister leg needs a bundled, " +
        "purpose-built self-registering test DLL that does not yet exist in this repo (follow-up). " +
        "A real system self-registering DLL was deliberately NOT substituted: its CLSID is already " +
        "registered by the OS before this test runs (so 'register -> assert present' proves nothing) " +
        "and unregistering a real system COM DLL is exactly the fragile-fixture risk the brief calls " +
        "out to avoid. When that fixture DLL lands it must be copied into the run's resolved " +
        "install_dir (SystemStepInstallDir) like the probe above, or PrivilegedTargetGuard refuses " +
        "it before DllRegisterServer is ever called. See the T13.1 report for the full writeup of " +
        "this decision.")]
    public Task Live_register_then_unregister_a_real_self_registering_dll() =>
        Task.CompletedTask;
}
