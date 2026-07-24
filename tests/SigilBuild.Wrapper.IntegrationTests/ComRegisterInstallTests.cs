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
/// the step's real end-to-end plumbing — resolve path, journal the inverse
/// BEFORE the native call, invoke <c>LoadLibraryEx</c>/<c>GetProcAddress</c>
/// through the AOT-safe function pointer, map the outcome to a
/// <see cref="StepResult"/>, and run the journaled
/// <see cref="RollbackRecord.UnregisterCom"/> undo — genuinely elevated, on
/// the CI VM, rather than only unit-tested unelevated (as
/// <c>ComRegisterStepTests</c> already does locally). It deliberately targets
/// <c>kernel32.dll</c> (present on every Windows host, guaranteed to have no
/// <c>DllRegisterServer</c> export) so it never touches real HKCR state,
/// while still proving elevation doesn't change the step's failure-path
/// behavior.
/// </para>
/// <para>
/// <b>Gating:</b> soft-skips (returns without asserting — the same convention
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
    private static bool ShouldRun() =>
        OperatingSystem.IsWindows() &&
        TestEnvironment.IsEnabled &&
        Environment.GetEnvironmentVariable("SIGIL_VM_SYSTEMSTEPS") == "1" &&
        Elevation.IsProcessElevated();

    [Fact]
    public async Task ComRegisterStep_runs_the_full_register_journal_reverse_plumbing_under_elevation()
    {
        if (!ShouldRun())
        {
            return; // soft-skip — see class remarks. Verified on the CI VM only.
        }

        // kernel32.dll: present on every Windows host, loads fine, but has no
        // DllRegisterServer export — never touches real HKCR/CLSID state, so
        // this is safe to run for real (not a soft no-op) even under
        // elevation. It still exercises the genuine LoadLibraryEx /
        // GetProcAddress / journal-then-invoke / rollback plumbing end to end.
        var dll = Path.Combine(Environment.SystemDirectory, "kernel32.dll");
        var spec = new InstallStep.ComRegister("it-comreg", dll, When: null, OnFailure: OnFailure.Continue);
        var journal = new RollbackJournal();

        var result = await new ComRegisterStep(spec)
            .RunAsync(StepContext.Empty, journal, default);

        result.Success.Should().BeFalse("kernel32.dll has no DllRegisterServer export");
        result.Error.Should().Contain("self-registering COM DLL");

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
        "out to avoid. See the T13.1 report for the full writeup of this decision.")]
    public Task Live_register_then_unregister_a_real_self_registering_dll() =>
        Task.CompletedTask;
}
