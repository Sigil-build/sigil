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
/// The live leg for <c>com_register</c>, deferred to CI-VM when that step shipped with
/// unit/parse/roundtrip coverage only.
/// </summary>
/// <remarks>
/// <para>
/// A genuine register → assert <c>HKCR\CLSID\{..}</c> appears → unregister → assert gone
/// leg needs a real self-registering COM DLL; none exists in this repo. A system DLL
/// already registered by the OS (e.g. <c>actxprxy.dll</c>) cannot serve: its CLSID
/// predates the test, so "register → assert present" would prove nothing, and
/// unregistering a real system COM DLL on a shared CI runner is unacceptable.
/// <see cref="Live_register_then_unregister_a_real_self_registering_dll"/> below is an
/// explicit <c>xunit</c> <c>Skip</c> (not OS/elevation-gated) whose reason spells out the
/// fixture needed: a tiny Native AOT DLL exporting both <c>DllRegisterServer</c> and
/// <c>DllUnregisterServer</c>, writing only a private test CLSID.
/// </para>
/// <para>
/// What runs live, in
/// <see cref="ComRegisterStep_refuses_an_export_less_dll_under_elevation_and_journals_no_undo"/>:
/// the step's real plumbing up to the register — resolve path, clear the
/// privileged-target anchor, invoke <c>LoadLibraryEx</c>/<c>GetProcAddress</c> through the
/// AOT-safe function pointer, map the outcome to a <see cref="StepResult"/> — genuinely
/// elevated, on the CI VM. The DLL has no <c>DllRegisterServer</c> export, so it never
/// touches real HKCR state while still proving elevation doesn't change the failure-path
/// behavior. Since nothing is registered, <c>UndoAsync</c> has nothing to reverse: the
/// journal must be EMPTY — <c>DllUnregisterServer</c> is the only probe a COM
/// registration has, and a failure there means "still registered" (R15).
/// </para>
/// <para>
/// The probe DLL is copied into a genuine machine-scope <c>install_dir</c>
/// (<see cref="SystemStepInstallDir"/>) rather than pointed at a system path via
/// <see cref="StepContext.Empty"/>: a SYSTEM-level step target outside any install
/// directory is the R3 attack (R3, R9, R16), and <c>com_register</c> is the sharpest of
/// the four privileged targets, since the DLL loads into the elevated installer process
/// itself. The copy is <c>winmm.dll</c> under a unique name, not <c>kernel32.dll</c>:
/// <c>kernel32.dll</c> is a <c>KnownDLLs</c> entry, so <c>LoadLibraryEx</c> on a copy of
/// it is satisfied by the already-mapped system module regardless of path — proving
/// nothing about the file inside <c>install_dir</c>. <c>winmm.dll</c> is not a
/// <c>KnownDLLs</c> entry, loads self-standing from an arbitrary directory, and exports no
/// <c>DllRegisterServer</c>, so the load genuinely comes from <c>install_dir</c> (R67).
/// </para>
/// <para>
/// <b>Gating:</b> reports a genuine Skipped result (via
/// <see cref="VmSystemStepsFactAttribute"/>, R6) unless the host is Windows,
/// <c>SIGIL_VM_TESTS=1</c> and <c>SIGIL_VM_SYSTEMSTEPS=1</c> are both set, AND the current
/// process is elevated (<see cref="Elevation.IsProcessElevated"/>). Not run locally in
/// this sandbox — the CI VM job (<c>p11-system-steps-vm</c> in
/// <c>wrapper-vm-tests.yml</c>) sets all three and runs on a real elevated
/// <c>windows-latest</c> runner.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public class ComRegisterInstallTests
{
    [VmSystemStepsFact]
    public async Task ComRegisterStep_refuses_an_export_less_dll_under_elevation_and_journals_no_undo()
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

        // Nothing was registered, so there is nothing to unregister: the journal must be
        // EMPTY (R15), which also means there is no undo left to drive here. See
        // ComRegisterStepTests for the unit-level version of this contract.
        journal.Records.Should().BeEmpty(
            "the probe DLL exports neither DllRegisterServer nor DllUnregisterServer, so a " +
            "journaled UnregisterCom would fail this install's rollback AND every later " +
            "uninstall attempt on the strength of a probe that never ran");
    }

    [Fact(Skip =
        "com_register's live register->assert HKCR\\CLSID->unregister leg needs a bundled, " +
        "purpose-built self-registering test DLL that does not yet exist in this repo (follow-up). " +
        "THE FIXTURE, PRECISELY: a native DLL exporting BOTH DllRegisterServer and " +
        "DllUnregisterServer as stdcall HRESULT(void), which write and remove ONLY one " +
        "test-specific CLSID under HKCR (e.g. HKCR\\CLSID\\{sigil-test GUID}) and touch nothing " +
        "else, with DllUnregisterServer returning S_OK when the key is already absent (idempotent, " +
        "so a re-run and a double rollback both stay clean). Buildable inside this repo as a tiny " +
        "Native AOT class library whose two entry points are " +
        "[UnmanagedCallersOnly(EntryPoint = \"DllRegisterServer\")] / \"DllUnregisterServer\" — no " +
        "C++ project needed, which matters because it must be CI-built: the maintainer box that " +
        "wrote this cannot AOT-publish (no MSVC C++ workload), so the DLL has to be produced by the " +
        "windows-latest job and staged for the VM leg rather than checked in as a binary. With that " +
        "in hand the leg asserts: register -> the CLSID key EXISTS -> replay the journaled " +
        "UnregisterCom -> the key is GONE and the undo did not throw (R15). " +
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
