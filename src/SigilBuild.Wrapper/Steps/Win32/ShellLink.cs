namespace SigilBuild.Wrapper.Steps.Win32;

using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;

/// <summary>
/// AOT-compatible wrapper around <c>IShellLinkW</c> + <c>IPersistFile</c> for
/// creating Windows <c>.lnk</c> shortcut files. All COM interop is
/// source-generated via <see cref="GeneratedComInterfaceAttribute"/>; no
/// <c>[ComImport]</c>, no runtime IL stubs, no reflection — this compiles
/// cleanly under <c>PublishAot=true</c> and <c>TrimMode=full</c>.
/// </summary>
/// <remarks>
/// <para>
/// The flow follows the canonical Win32 pattern: <c>CoInitializeEx</c> →
/// <c>CoCreateInstance(CLSID_ShellLink, IID_IShellLinkW)</c> → set fields →
/// cast to <see cref="IPersistFile"/> (the source generator builds the
/// <c>QueryInterface</c> dispatch on the cast) → <c>IPersistFile.Save</c> →
/// release → <c>CoUninitialize</c>.
/// </para>
/// <para>
/// Task 16 ships <see cref="Save"/> only. Round-trip read-back of <c>.lnk</c>
/// fields is intentionally deferred: <c>IShellLinkW</c>'s <c>Get*</c> methods
/// take caller-allocated <c>wchar_t*</c> buffers, which are awkward to express
/// across the source generator's marshalling defaults without bespoke custom
/// marshallers. Tests instead validate that <see cref="Save"/> wrote a
/// well-formed <c>.lnk</c> via the file-magic smoke check
/// (first 4 bytes <c>4C 00 00 00</c> — the Shell Link Header signature).
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class ShellLink
{
    private static readonly Guid CLSID_ShellLink =
        new("00021401-0000-0000-C000-000000000046");

    // CLSCTX_INPROC_SERVER — the shell link CLSID lives in shell32.dll.
    private const uint CLSCTX_INPROC_SERVER = 0x1;

    // COINIT_APARTMENTTHREADED — shell COM objects expect STA.
    private const uint COINIT_APARTMENTTHREADED = 0x2;

    // STGM_READ — IPersistFile::Load mode for the read path. Unused while
    // Read is deferred but retained as documentation of the contract.
    // private const uint STGM_READ = 0x0;

    // Single ComWrappers instance — StrategyBasedComWrappers is stateless and
    // safe to share across calls.
    private static readonly StrategyBasedComWrappers s_wrappers = new();

    /// <summary>
    /// Create a <c>.lnk</c> file at <paramref name="lnkPath"/> pointing at
    /// <paramref name="target"/>. Optional fields (<paramref name="arguments"/>,
    /// <paramref name="workingDirectory"/>, <paramref name="iconLocation"/>,
    /// <paramref name="description"/>) are only set on the link when non-empty.
    /// </summary>
    /// <exception cref="System.ComponentModel.Win32Exception">
    /// Thrown when <c>CoCreateInstance</c> or <c>IPersistFile.Save</c> reports
    /// an HRESULT failure.
    /// </exception>
    public static void Save(
        string lnkPath,
        string target,
        string? arguments = null,
        string? workingDirectory = null,
        string? iconLocation = null,
        string? description = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(lnkPath);
        ArgumentException.ThrowIfNullOrEmpty(target);

        var coInitHr = CoMarshalImports.CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED);
        // S_OK (0), S_FALSE (1, "already initialized on this thread") and
        // RPC_E_CHANGED_MODE (0x80010106, "thread already STA/MTA differently")
        // are all OK to proceed under — for RPC_E_CHANGED_MODE the existing
        // apartment is reused. We only abort on hard failure HRESULTs.
        var ownsComInit = coInitHr == 0 || coInitHr == 1;
        if (coInitHr < 0 && coInitHr != unchecked((int)0x80010106))
        {
            throw new System.ComponentModel.Win32Exception(
                coInitHr,
                $"CoInitializeEx failed (0x{coInitHr:X8})");
        }

        IntPtr unknown = IntPtr.Zero;
        try
        {
            var clsid = CLSID_ShellLink;
            var iidIUnknown = new Guid("00000000-0000-0000-C000-000000000046");
            var hr = CoMarshalImports.CoCreateInstance(
                in clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER, in iidIUnknown, out unknown);
            if (hr < 0 || unknown == IntPtr.Zero)
            {
                throw new System.ComponentModel.Win32Exception(
                    hr, $"CoCreateInstance(CLSID_ShellLink) failed (0x{hr:X8})");
            }

            // GetOrCreateObjectForComInstance handles QueryInterface for any
            // [GeneratedComInterface] this RCW exposes — IShellLinkW and
            // IPersistFile both come back from the same managed wrapper.
            var rcw = s_wrappers.GetOrCreateObjectForComInstance(unknown, CreateObjectFlags.None);

            try
            {
                var link = (IShellLinkW)rcw;
                link.SetPath(target);

                if (!string.IsNullOrEmpty(arguments))
                {
                    link.SetArguments(arguments);
                }
                if (!string.IsNullOrEmpty(workingDirectory))
                {
                    link.SetWorkingDirectory(workingDirectory);
                }
                if (!string.IsNullOrEmpty(iconLocation))
                {
                    // Index 0 is the convention for "first icon in the file";
                    // callers wanting a different index can pass "path,index"
                    // — split here is intentionally not done so that non-PE
                    // icon files (.ico) round-trip without parsing.
                    link.SetIconLocation(iconLocation, 0);
                }
                if (!string.IsNullOrEmpty(description))
                {
                    link.SetDescription(description);
                }

                var persist = (IPersistFile)rcw;
                persist.Save(lnkPath, fRemember: true);
            }
            finally
            {
                // Drop the managed RCW; the underlying IUnknown ref is
                // released alongside `unknown` in the outer finally.
                if (rcw is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
        }
        finally
        {
            if (unknown != IntPtr.Zero)
            {
                Marshal.Release(unknown);
            }
            if (ownsComInit)
            {
                CoMarshalImports.CoUninitialize();
            }
        }
    }

    /// <summary>
    /// Smoke-check that a file at <paramref name="lnkPath"/> looks like a
    /// Windows Shell Link: existence + non-zero length + the 4-byte
    /// <c>4C 00 00 00</c> Shell Link Header magic. Used by tests in lieu of
    /// a full <c>IShellLinkW.GetPath</c> round-trip (see remarks on the type).
    /// </summary>
    public static bool LooksLikeShellLink(string lnkPath)
    {
        if (!File.Exists(lnkPath))
        {
            return false;
        }
        var info = new FileInfo(lnkPath);
        if (info.Length < 4)
        {
            return false;
        }

        Span<byte> magic = stackalloc byte[4];
        using var fs = File.OpenRead(lnkPath);
        var read = fs.Read(magic);
        return read == 4
            && magic[0] == 0x4C
            && magic[1] == 0x00
            && magic[2] == 0x00
            && magic[3] == 0x00;
    }

    // Suppress IDE0051 / unused-warning by referencing the field through
    // a no-op accessor; helps the AOT compiler keep this culture invariant
    // available if a future hex-formatting overload pulls it in.
    internal static string FormatHResult(int hr) =>
        "0x" + hr.ToString("X8", CultureInfo.InvariantCulture);
}

/// <summary>
/// AOT-friendly subset of <c>IShellLinkW</c> (shobjidl_core.h). Only the
/// setter methods needed by <see cref="ShellLink.Save"/> are declared; the
/// generator builds the full vtable order from method declaration order, so
/// every slot up to the highest setter we use must be present.
/// </summary>
/// <remarks>
/// Vtable order (from <c>shobjidl_core.h</c>):
/// <c>0:GetPath, 1:GetIDList, 2:SetIDList, 3:GetDescription, 4:SetDescription,
/// 5:GetWorkingDirectory, 6:SetWorkingDirectory, 7:GetArguments, 8:SetArguments,
/// 9:GetHotkey, 10:SetHotkey, 11:GetShowCmd, 12:SetShowCmd, 13:GetIconLocation,
/// 14:SetIconLocation, 15:SetRelativePath, 16:Resolve, 17:SetPath</c>.
/// The Get* slots are declared with <c>nint</c> buffer pointers (effectively
/// "any caller-supplied wchar_t*") to satisfy the source generator without
/// pulling in a string marshaller for the read path; we never call them in
/// Task 16 — they exist purely to anchor the vtable layout.
/// </remarks>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("000214F9-0000-0000-C000-000000000046")]
[SupportedOSPlatform("windows")]
internal partial interface IShellLinkW
{
    void GetPath(IntPtr pszFile, int cchMaxPath, IntPtr pfd, uint fFlags);

    void GetIDList(out IntPtr ppidl);

    void SetIDList(IntPtr pidl);

    void GetDescription(IntPtr pszName, int cchMaxName);

    void SetDescription(string pszName);

    void GetWorkingDirectory(IntPtr pszDir, int cchMaxPath);

    void SetWorkingDirectory(string pszDir);

    void GetArguments(IntPtr pszArgs, int cchMaxPath);

    void SetArguments(string pszArgs);

    void GetHotkey(out short pwHotkey);

    void SetHotkey(short wHotkey);

    void GetShowCmd(out int piShowCmd);

    void SetShowCmd(int iShowCmd);

    void GetIconLocation(IntPtr pszIconPath, int cchIconPath, out int piIcon);

    void SetIconLocation(string pszIconPath, int iIcon);

    void SetRelativePath(string pszPathRel, uint dwReserved);

    void Resolve(IntPtr hwnd, uint fFlags);

    void SetPath(string pszFile);
}

/// <summary>
/// AOT-friendly <c>IPersistFile</c> declaration. Inherits IPersist's
/// <c>GetClassID</c> slot first, then IPersistFile's own methods; declaration
/// order must match the native vtable.
/// </summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("0000010b-0000-0000-C000-000000000046")]
[SupportedOSPlatform("windows")]
internal partial interface IPersistFile
{
    // IPersist::GetClassID
    void GetClassID(out Guid pClassID);

    // IPersistFile::IsDirty — preserve the HRESULT (S_OK / S_FALSE).
    [PreserveSig]
    int IsDirty();

    void Load(string pszFileName, uint dwMode);

    void Save(string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);

    void SaveCompleted(string pszFileName);

    // GetCurFile takes a CoTaskMem-allocated wchar_t** out; we model it as
    // an IntPtr so we don't accidentally drop a string marshaller into the
    // vtable. Not called in Task 16 — present only to anchor slot order.
    void GetCurFile(out IntPtr ppszFileName);
}

/// <summary>
/// P/Invokes for COM apartment + activation. <c>[LibraryImport]</c> ensures
/// the marshalling stub is source-generated, keeping us AOT-compatible.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class CoMarshalImports
{
    [LibraryImport("ole32.dll", EntryPoint = "CoInitializeEx", SetLastError = false)]
    public static partial int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [LibraryImport("ole32.dll", EntryPoint = "CoUninitialize")]
    public static partial void CoUninitialize();

    [LibraryImport("ole32.dll", EntryPoint = "CoCreateInstance")]
    public static partial int CoCreateInstance(
        in Guid rclsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        in Guid riid,
        out IntPtr ppv);
}
