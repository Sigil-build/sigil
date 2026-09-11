namespace SigilBuild.Wrapper.Steps;

using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Steps.Win32;

/// <summary>
/// Machine-scope-only <c>com_register</c> step, and the one AOT-risk step in the
/// catalog. Self-registers a COM DLL by loading it and invoking its exported
/// <c>HRESULT DllRegisterServer(void)</c> through a C# unmanaged function
/// pointer (see <see cref="ComRegistration"/>). Because <c>DllRegisterServer</c>
/// writes machine-global registration (<c>HKLM\Software\Classes</c> /
/// <c>HKCR\CLSID</c>), the step overrides
/// <see cref="InstallStep.RequiresMachineScope"/> to <c>true</c> (see
/// <see cref="SigilBuild.Core.Configuration.MachineScopeGuard"/> / SIG0310).
/// Records a <see cref="RollbackRecord.UnregisterCom"/> BEFORE the register so a
/// mid-install crash and <c>setup.exe /Uninstall</c> both call
/// <c>DllUnregisterServer</c> — mirrors <see cref="ServiceInstallStep"/>'s
/// <c>RemoveService</c> pattern — and <b>withdraws it again when that undo cannot
/// be called at all</b>, since replaying it could then only report a registration
/// it never managed to probe.
/// </summary>
/// <remarks>
/// <para>
/// The native load → resolve export → invoke → FreeLibrary path lives entirely
/// in <see cref="ComRegistration"/> so the same code serves both this step and
/// the undo record. This step's job is only to resolve the path, journal the
/// inverse, and map the <see cref="ComRegistration.ComInvocationResult"/> onto a
/// <see cref="StepResult"/>. The live register→assert-HKCR-CLSID→unregister leg
/// needs a real self-registering DLL plus admin and runs on the CI VM; the
/// load-failure and missing-export mappings are unit-tested locally on Windows
/// without admin.
/// </para>
/// <para>
/// <b>Journal-before is not journal-unconditionally.</b> Where
/// <c>RemoveService</c>, <c>DeleteScheduledTask</c> and <c>DeleteFirewallRule</c>
/// can each ask the OS at undo time whether their object exists — so an intent
/// record for a mutation that never happened replays as a silent no-op —
/// <see cref="RollbackRecord.UnregisterCom"/> cannot: its only probe is
/// <c>DllUnregisterServer</c> itself, and a failure there is reported as "the
/// registration is still in place" (R15). So when that probe <em>cannot be called at
/// all</em> — <see cref="ComRegistration.ComExportOutcome.LoadFailed"/> (the module
/// will not load) or <see cref="ComRegistration.ComExportOutcome.ExportMissing"/>
/// (it exports no such function) — the record is withdrawn via
/// <see cref="RollbackJournal.RetractLast"/>: it could only ever produce a false
/// "still registered" report. A failing HRESULT is <b>not</b> withdrawn — the
/// export ran and may have registered part of itself, and its
/// <c>DllUnregisterServer</c> is callable.
/// </para>
/// <para>
/// Note what is deliberately NOT claimed: that nothing was written. Both withdrawn
/// outcomes can have run <c>DllMain</c> as administrator first (a load failure is
/// exactly what <c>LoadLibraryEx</c> returns when <c>DllMain</c> returns FALSE
/// <em>after</em> executing). Sigil cannot prove such a DLL wrote nothing; it can
/// only establish that the undo it is holding is unusable. Publishers are told not
/// to register from <c>DllMain</c> (<c>docs/guides/install-steps.md</c>).
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class ComRegisterStep : IStep
{
    private readonly InstallStep.ComRegister _spec;
    private readonly ComRegistration.ComExportInvoker _invoke;

    public ComRegisterStep(InstallStep.ComRegister spec)
        : this(spec, ComRegistration.Invoke)
    {
    }

    /// <summary>
    /// Test seam: substitute the native invocation so the <b>retention</b> half of the
    /// journal contract can be pinned. The two withdrawn outcomes are reachable
    /// locally with real DLLs (a missing file, and <c>kernel32.dll</c>), but
    /// <see cref="ComRegistration.ComExportOutcome.Ok"/> and
    /// <see cref="ComRegistration.ComExportOutcome.HResultFailure"/> both need a real
    /// self-registering DLL that does not exist in this repo yet — so without a seam,
    /// widening the withdrawal to include <c>HResultFailure</c> (which would silently
    /// throw away the undo for a registration that partly happened) would pass the
    /// entire suite. A named non-generic delegate, statically bound, no reflection:
    /// AOT-safe.
    /// </summary>
    internal ComRegisterStep(InstallStep.ComRegister spec, ComRegistration.ComExportInvoker invoke)
    {
        _spec = spec;
        _invoke = invoke;
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

        // This DLL is loaded into the ELEVATED installer process and its
        // DllRegisterServer export is invoked, so a user-writable path here is
        // straight code execution as administrator. Anchored inside install_dir and
        // required to sit in an admin-only-writable directory, before the journal
        // entry — a refused step must not queue an UnregisterCom for a registration
        // it never made (R3, R9).
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

        var result = _invoke(path, "DllRegisterServer");

        // Withdraw the undo when its own probe CANNOT BE CALLED.
        // LoadFailed: the module will not load, so DllUnregisterServer cannot run.
        // ExportMissing: no DllRegisterServer export, hence near-certainly no
        // DllUnregisterServer either.
        //
        // DllUnregisterServer is the ONLY way to probe a COM registration, so a
        // failure there means "still registered" and fails the rollback/uninstall
        // (R15). For these two outcomes that failure is guaranteed and content-free —
        // a report of an unremovable machine-global registration issued on the
        // strength of a probe that never ran. NOT claimed: that nothing was written;
        // both outcomes can have executed DllMain as administrator first
        // (LoadLibraryEx returns NULL precisely when DllMain returns FALSE, after
        // running). The bar is the undo's feasibility, not the mutation's absence.
        // HResultFailure stays journaled: the export ran, may have written part of
        // its registration, and its DllUnregisterServer IS callable.
        if (result.Outcome is ComRegistration.ComExportOutcome.LoadFailed
            or ComRegistration.ComExportOutcome.ExportMissing)
        {
            // A false return is unreachable today — Append and RetractLast bracket
            // one synchronous stretch with no other journal writer in between — but
            // it IS exactly the regression this guard prevents: the record survives,
            // and then every uninstall of this app fails on a registration that was
            // never made. So it must never be silent. Loud in Debug (a future
            // interleaving breaks the test run), reported on the run's own diagnostic
            // channel in Release, where it reaches the wizard log pane and the /LOG
            // file.
            const string RetractionFailed =
                "com_register: internal error — the UnregisterCom rollback record could not be " +
                "withdrawn after a register whose undo cannot be called. Uninstall may report a " +
                "COM registration that does not exist; please report this.";

            var withdrawn = journal.RetractLast(undo);
            System.Diagnostics.Debug.Assert(withdrawn, RetractionFailed);
            if (!withdrawn)
            {
                ctx.ProgressSink?.Report(new StepProgress(0, 0, RetractionFailed, true));
            }
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
