using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Json;

namespace SigilBuild.Wrapper.Engine;

/// <summary>
/// Embedded blob describing the parameters and step list for the wrapper.
/// At pack time, <c>WrapperResourceWriter</c> embeds this as a Win32 resource
/// in the wrapper exe; at install time, <see cref="LoadFromSelf"/> reads it
/// back.
/// </summary>
internal sealed partial record WrapperBlob(
    string AppId,
    AppMetadata App,
    IReadOnlyList<ParameterDefinition> Parameters,
    IReadOnlyList<InstallStep> InstallSteps,
    IReadOnlyList<InstallStep> PreInstall,
    IReadOnlyList<InstallStep> PostInstall,
    IReadOnlyList<InstallStep> UpdateSteps,
    IReadOnlyList<InstallStep> Uninstall,
    bool IsUninstaller)
{
    /// <summary>
    /// Empty sentinel blob: well-known <c>AppId</c> placeholder and zero-length
    /// step / parameter lists. Returned by <see cref="LoadFromSelf"/> when the
    /// running module has no <c>SIGIL_BLOB_V1</c> resource embedded — the
    /// development / smoke-test scenario (e.g. running the un-stamped AOT
    /// runtime with <c>--version</c>).
    /// </summary>
    public static WrapperBlob Empty { get; } = new(
        AppId: "<unset>",
        App: AppMetadata.Empty,
        Parameters: Array.Empty<ParameterDefinition>(),
        InstallSteps: Array.Empty<InstallStep>(),
        PreInstall: Array.Empty<InstallStep>(),
        PostInstall: Array.Empty<InstallStep>(),
        UpdateSteps: Array.Empty<InstallStep>(),
        Uninstall: Array.Empty<InstallStep>(),
        IsUninstaller: false);

    /// <summary>
    /// Read the blob from the running executable's embedded Win32 resource.
    /// </summary>
    /// <remarks>
    /// Uses <c>FindResource</c> / <c>LoadResource</c> / <c>LockResource</c> /
    /// <c>SizeofResource</c> on the current module. If the <c>SIGIL_BLOB_V1</c>
    /// resource isn't present (un-stamped runtime, e.g. running
    /// <c>--version</c> directly against the AOT publish output) the method
    /// returns <see cref="Empty"/> rather than throwing — this keeps
    /// <see cref="Program.Main"/> usable in dev/test smoke runs.
    /// </remarks>
    public static WrapperBlob LoadFromSelf()
    {
        var bytes = TryReadResource(BlobResourceName);
        if (bytes is null) return Empty;

        var json = System.Text.Encoding.UTF8.GetString(bytes);
        var serializable = System.Text.Json.JsonSerializer.Deserialize(
            json, WrapperBlobJsonContext.Default.SerializableWrapperBlob);
        return serializable is null
            ? Empty
            : SerializableWrapperBlob.ToWrapperBlob(serializable);
    }

    /// <summary>
    /// Read the embedded payload bytes (<c>SIGIL_PAYLOAD_V1</c>) from the
    /// running executable. Returns an empty array when the resource isn't
    /// embedded, mirroring <see cref="LoadFromSelf"/>'s graceful-fallback
    /// behaviour for un-stamped runtimes.
    /// </summary>
    /// <remarks>
    /// Payload extraction (uncompressing a zip into a temp dir for
    /// <c>payload://</c>-prefixed file_copy sources) is an
    /// <c>InstallEngine</c> concern landing in Tasks 15+. Task 14 only
    /// exposes the raw bytes.
    /// </remarks>
    public static byte[] LoadPayloadBytes()
    {
        return TryReadResource(PayloadResourceName) ?? Array.Empty<byte>();
    }

    /// <summary>
    /// Read the embedded installer-host bundle bytes
    /// (<c>SIGIL_INSTALLER_HOST_V1</c>) from the running executable. Returns
    /// <c>null</c> when the resource isn't embedded — manifests without an
    /// <c>installer:</c> block produce a headless-only setup.exe. The wrapper
    /// runtime treats null as "no wizard available" and falls through to
    /// running install_steps directly.
    /// </summary>
    public static byte[]? LoadInstallerHostBundleBytes()
    {
        return TryReadResource(InstallerHostResourceName);
    }

    /// <summary>
    /// Read the embedded uninstaller.exe bytes (<c>SIGIL_UNINSTALLER_V1</c>)
    /// from the running executable. Returns <c>null</c> when no uninstaller is
    /// embedded — manifests without an <c>uninstall:</c> block, or the
    /// uninstaller.exe itself (it doesn't embed a copy of itself).
    /// </summary>
    public static byte[]? LoadUninstallerExeBytes()
    {
        return TryReadResource(UninstallerResourceName);
    }

    private const string BlobResourceName = "SIGIL_BLOB_V1";
    private const string PayloadResourceName = "SIGIL_PAYLOAD_V1";
    private const string InstallerHostResourceName = "SIGIL_INSTALLER_HOST_V1";
    private const string UninstallerResourceName = "SIGIL_UNINSTALLER_V1";

    // RT_RCDATA — application-defined raw data resource (winuser.h).
    private static readonly IntPtr RtRcData = (IntPtr)10;

    private static byte[]? TryReadResource(string name)
    {
        // Current module: GetModuleHandleW(null) returns the running exe.
        var hModule = GetModuleHandleW(IntPtr.Zero);
        if (hModule == IntPtr.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "GetModuleHandle(null) failed");
        }

        var namePtr = Marshal.StringToHGlobalUni(name);
        try
        {
            var hRes = FindResourceW(hModule, namePtr, RtRcData);
            if (hRes == IntPtr.Zero)
            {
                // The expected "resource not stamped yet" path — caller
                // decides how to surface (Empty blob, empty payload).
                return null;
            }

            var size = SizeofResource(hModule, hRes);
            if (size == 0)
            {
                return Array.Empty<byte>();
            }

            var hData = LoadResource(hModule, hRes);
            if (hData == IntPtr.Zero)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"LoadResource failed for '{name}'");
            }

            var ptr = LockResource(hData);
            if (ptr == IntPtr.Zero)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"LockResource failed for '{name}'");
            }

            var managed = new byte[size];
            Marshal.Copy(ptr, managed, 0, (int)size);
            return managed;
        }
        finally
        {
            Marshal.FreeHGlobal(namePtr);
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr GetModuleHandleW(IntPtr lpModuleName);

    [LibraryImport("kernel32.dll", EntryPoint = "FindResourceW",
        SetLastError = true)]
    private static partial IntPtr FindResourceW(IntPtr hModule, IntPtr lpName, IntPtr lpType);

    [LibraryImport("kernel32.dll", EntryPoint = "LoadResource",
        SetLastError = true)]
    private static partial IntPtr LoadResource(IntPtr hModule, IntPtr hResInfo);

    [LibraryImport("kernel32.dll", EntryPoint = "LockResource",
        SetLastError = true)]
    private static partial IntPtr LockResource(IntPtr hResData);

    [LibraryImport("kernel32.dll", EntryPoint = "SizeofResource",
        SetLastError = true)]
    private static partial uint SizeofResource(IntPtr hModule, IntPtr hResInfo);
}
