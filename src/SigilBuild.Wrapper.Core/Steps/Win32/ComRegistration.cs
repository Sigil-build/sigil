namespace SigilBuild.Wrapper.Steps.Win32;

using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

/// <summary>
/// P11 (T11.2) — the one AOT-risk primitive in P11. Loads a COM DLL and invokes
/// its exported <c>HRESULT DllRegisterServer(void)</c> /
/// <c>DllUnregisterServer(void)</c> through a <b>C# unmanaged function
/// pointer</b> (<c>delegate* unmanaged[Stdcall]&lt;int&gt;</c>). That idiom is
/// statically bound at compile time — it carries no reflection, no runtime IL
/// stub, and no <c>Marshal.GetDelegateForFunctionPointer</c> — so it is
/// unambiguously safe under <c>PublishAot=true</c> / <c>TrimMode=full</c>. All
/// three <c>[LibraryImport]</c>s follow the repo's established idiom
/// (source-generated stubs, <c>SetLastError</c>; see
/// <see cref="SigilBuild.Wrapper.Engine.NativeRuntimeBootstrap"/>).
/// </summary>
/// <remarks>
/// A single native path serves both callers: the <c>com_register</c> install
/// step (which maps each outcome to a <c>StepResult</c>) and the best-effort
/// undo (<c>RollbackRecord.UnregisterCom</c>, which ignores the outcome — a
/// missing export or non-zero HRESULT on unregister is tolerated, like
/// <c>RemoveService</c> tolerates a missing service). Because both the load
/// failure and the missing-export cases are normal, expected results rather
/// than exceptional ones, they are surfaced via <see cref="ComInvocationResult"/>
/// instead of thrown exceptions. <c>FreeLibrary</c> always runs in a
/// <c>finally</c>.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static partial class ComRegistration
{
    // LOAD_WITH_ALTERED_SEARCH_PATH: resolve the COM DLL's own dependencies
    // relative to its directory rather than the host process directory — the
    // standard flag for loading a self-contained plug-in DLL by full path.
    private const uint LOAD_WITH_ALTERED_SEARCH_PATH = 0x8;

    internal enum ComExportOutcome
    {
        /// <summary>Export found and returned S_OK (0).</summary>
        Ok,

        /// <summary>LoadLibraryEx returned NULL — the DLL or a dependency
        /// could not be loaded. <c>Win32Error</c> is set.</summary>
        LoadFailed,

        /// <summary>GetProcAddress returned NULL — the DLL has no such export
        /// (not a self-registering COM DLL).</summary>
        ExportMissing,

        /// <summary>Export was invoked and returned a failure HRESULT.
        /// <c>HResult</c> is set.</summary>
        HResultFailure,
    }

    internal readonly record struct ComInvocationResult(
        ComExportOutcome Outcome, int Win32Error, int HResult);

    /// <summary>
    /// Loads <paramref name="dllPath"/>, resolves the stdcall
    /// <c>HRESULT <paramref name="export"/>(void)</c> export, invokes it via a
    /// C# unmanaged function pointer, and always <c>FreeLibrary</c> in a
    /// <c>finally</c>. Never throws for the expected COM failure modes — they
    /// are returned via <see cref="ComInvocationResult"/>.
    /// </summary>
    internal static ComInvocationResult Invoke(string dllPath, string export)
    {
        var hModule = LoadLibraryExW(dllPath, IntPtr.Zero, LOAD_WITH_ALTERED_SEARCH_PATH);
        if (hModule == IntPtr.Zero)
        {
            return new ComInvocationResult(
                ComExportOutcome.LoadFailed, Marshal.GetLastWin32Error(), 0);
        }

        try
        {
            var proc = GetProcAddress(hModule, export);
            if (proc == IntPtr.Zero)
            {
                return new ComInvocationResult(ComExportOutcome.ExportMissing, 0, 0);
            }

            int hr;
            unsafe
            {
                // HRESULT <export>(void), __stdcall, no args — invoked through a
                // C# unmanaged function pointer. Statically bound at compile
                // time (the unsafe block is scoped to exactly this cast+call),
                // so there is no reflection / IL-stub AOT risk. Preferred over
                // Marshal.GetDelegateForFunctionPointer per the AOT-safety skill.
                hr = ((delegate* unmanaged[Stdcall]<int>)proc)();
            }

            return new ComInvocationResult(
                hr == 0 ? ComExportOutcome.Ok : ComExportOutcome.HResultFailure, 0, hr);
        }
        finally
        {
            FreeLibrary(hModule);
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "LoadLibraryExW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr LoadLibraryExW(string lpLibFileName, IntPtr hFile, uint dwFlags);

    // GetProcAddress has no wide variant — the export name is always ANSI.
    [LibraryImport("kernel32.dll", EntryPoint = "GetProcAddress", SetLastError = true)]
    private static partial IntPtr GetProcAddress(
        IntPtr hModule, [MarshalAs(UnmanagedType.LPStr)] string lpProcName);

    [LibraryImport("kernel32.dll", EntryPoint = "FreeLibrary", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeLibrary(IntPtr hModule);
}
