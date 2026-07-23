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
/// <c>RemoveService</c> pattern.
/// </summary>
/// <remarks>
/// The native load → resolve export → invoke → FreeLibrary path lives entirely
/// in <see cref="ComRegistration"/> so the same code serves both this step and
/// the undo record. This step's job is only to resolve the path, journal the
/// inverse, and map the <see cref="ComRegistration.ComInvocationResult"/> onto a
/// <see cref="StepResult"/>. The live register→assert-HKCR-CLSID→unregister leg
/// needs a real self-registering DLL plus admin and is verified on the CI VM
/// (AGENTS.md §2); the load-failure and missing-export mappings are unit-tested
/// locally on Windows without admin.
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

        // Path may reference the extracted payload (payload://) or {install_dir}.
        var path = ctx.ResolvePath(_spec.Path);
        if (string.IsNullOrWhiteSpace(path))
        {
            return Task.FromResult(StepResult.Failed("com_register: path is empty after substitution"));
        }

        // Journal the inverse (DllUnregisterServer) BEFORE registering so an
        // interrupted install and /Uninstall both unwind the COM registration.
        // Path only — no secrets.
        journal.Append(new RollbackRecord.UnregisterCom(path));

        var result = ComRegistration.Invoke(path, "DllRegisterServer");
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
