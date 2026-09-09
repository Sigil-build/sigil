namespace SigilBuild.Wrapper.Steps;

using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Steps.Win32;

/// <summary>
/// P11 (T11.2) machine-scope-only <c>com_register</c> step — the one AOT-risk
/// step in P11. Self-registers a COM DLL by loading it and invoking its exported
/// <c>HRESULT DllRegisterServer(void)</c> through a C# unmanaged function
/// pointer (see <see cref="ComRegistration"/>). Because <c>DllRegisterServer</c>
/// writes machine-global registration (<c>HKLM\Software\Classes</c> /
/// <c>HKCR\CLSID</c>), the step overrides
/// <see cref="InstallStep.RequiresMachineScope"/> to <c>true</c> (see
/// <see cref="SigilBuild.Core.Configuration.MachineScopeGuard"/> / SIG0310).
/// Records a <see cref="RollbackRecord.UnregisterCom"/> BEFORE the register so a
/// mid-install crash and <c>setup.exe /Uninstall</c> both call
/// <c>DllUnregisterServer</c> — mirrors <see cref="ServiceInstallStep"/>'s
/// <c>RemoveService</c> pattern — and <b>withdraws it again when the register
/// provably did not take effect</b>.
/// </summary>
/// <remarks>
/// <para>
/// The native load → resolve export → invoke → FreeLibrary path lives entirely
/// in <see cref="ComRegistration"/> so the same code serves both this step and
/// the undo record. This step's job is only to resolve the path, journal the
/// inverse, and map the <see cref="ComRegistration.ComInvocationResult"/> onto a
/// <see cref="StepResult"/>. The live register→assert-HKCR-CLSID→unregister leg
/// needs a real self-registering DLL plus admin and is verified on the CI VM
/// (AGENTS.md §2); the load-failure and missing-export mappings are unit-tested
/// locally on Windows without admin.
/// </para>
/// <para>
/// <b>Journal-before is not journal-unconditionally.</b> Where
/// <c>RemoveService</c>, <c>DeleteScheduledTask</c> and <c>DeleteFirewallRule</c>
/// can each ask the OS at undo time whether their object exists — so an intent
/// record for a mutation that never happened replays as a silent no-op —
/// <see cref="RollbackRecord.UnregisterCom"/> cannot: since R15 its only probe is
/// <c>DllUnregisterServer</c> itself, and a failure there is reported as "the
/// registration is still in place". So when the outcome proves nothing was
/// registered (<see cref="ComRegistration.ComExportOutcome.LoadFailed"/> — the
/// module never mapped; <see cref="ComRegistration.ComExportOutcome.ExportMissing"/>
/// — there is no <c>DllRegisterServer</c> to have run), the record is withdrawn
/// via <see cref="RollbackJournal.RetractLast"/>. A failing HRESULT is not
/// withdrawn: the export ran and may have registered part of itself.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class ComRegisterStep : IStep
{
    private readonly InstallStep.ComRegister _spec;

    public ComRegisterStep(InstallStep.ComRegister spec)
    {
        _spec = spec;
    }

    public Task<StepResult> RunAsync(StepContext ctx, RollbackJournal journal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(journal);

        // Path must resolve INSIDE install_dir. A payload:// value rebases onto the
        // extraction temp directory, which is user-writable and is deleted when the
        // run ends, so the guard below refuses it — file_copy the DLL into
        // install_dir first. See PrivilegedTargetGuard's remarks.
        var path = ctx.ResolvePath(_spec.Path);
        if (string.IsNullOrWhiteSpace(path))
        {
            return Task.FromResult(StepResult.Failed("com_register: path is empty after substitution"));
        }

        // R3/R9: this DLL is loaded into the ELEVATED installer process and its
        // DllRegisterServer export is invoked, so a user-writable path here is
        // straight code execution as administrator. Anchored inside install_dir
        // and required to sit in an admin-only-writable directory, before the
        // journal entry — a refused step must not queue an UnregisterCom for a
        // registration it never made.
        var refusal = PrivilegedTargetGuard.Check("com_register", "path", ctx.InstallDir, path);
        if (refusal is not null)
        {
            return Task.FromResult(StepResult.Failed(refusal));
        }

        // Journal the inverse (DllUnregisterServer) BEFORE registering so an
        // interrupted install and /Uninstall both unwind the COM registration.
        // Path only — no secrets.
        var undo = new RollbackRecord.UnregisterCom(path);
        journal.Append(undo);

        var result = ComRegistration.Invoke(path, "DllRegisterServer");

        // A registration that PROVABLY never took effect must journal nothing.
        // LoadFailed: the module never mapped, so DllRegisterServer never ran.
        // ExportMissing: it mapped but has no DllRegisterServer to run — and
        // therefore, near-certainly, no DllUnregisterServer either.
        //
        // Since R15 the undo no longer swallows its outcome: DllUnregisterServer
        // is the ONLY way to probe a COM registration, so a failure there means
        // "still registered" and fails the rollback/uninstall. Leaving the record
        // in for these two outcomes would guarantee that failure on a DLL that
        // registered nothing — the install fails, and then its own cleanup
        // reports an unremovable machine-global registration that never existed.
        // Withdraw it. HResultFailure is deliberately NOT withdrawn: the export
        // did run and may have written part of its registration before failing,
        // so the record stays and the undo does its best (R15's "unknown is not
        // grounds for silence").
        if (result.Outcome is ComRegistration.ComExportOutcome.LoadFailed
            or ComRegistration.ComExportOutcome.ExportMissing)
        {
            journal.RetractLast(undo);
        }

        return Task.FromResult(result.Outcome switch
        {
            ComRegistration.ComExportOutcome.Ok => StepResult.Ok(),

            ComRegistration.ComExportOutcome.LoadFailed => StepResult.Failed(
                $"com_register: LoadLibraryEx('{path}') failed (Win32 error {result.Win32Error}); " +
                "the COM DLL or one of its dependencies could not be loaded"),

            ComRegistration.ComExportOutcome.ExportMissing => StepResult.Failed(
                $"com_register: '{path}' is not a self-registering COM DLL / has no DllRegisterServer export"),

            _ => StepResult.Failed(
                $"com_register: DllRegisterServer('{path}') returned HRESULT 0x{result.HResult:X8}"),
        });
    }
}
