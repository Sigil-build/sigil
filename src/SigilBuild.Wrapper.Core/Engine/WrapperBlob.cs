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
    IReadOnlyList<ParameterDefinition> Parameters,
    IReadOnlyList<InstallStep> InstallSteps,
    IReadOnlyList<InstallStep> PreInstall,
    IReadOnlyList<InstallStep> PostInstall,
    IReadOnlyList<InstallStep> UpdateSteps)
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
        Parameters: Array.Empty<ParameterDefinition>(),
        InstallSteps: Array.Empty<InstallStep>(),
        PreInstall: Array.Empty<InstallStep>(),
        PostInstall: Array.Empty<InstallStep>(),
        UpdateSteps: Array.Empty<InstallStep>());

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
    /// Read only the brand data (derived light/dark token maps, base64 logo/hero,
    /// ARP display fields) from the embedded <c>SIGIL_BLOB_V1</c> resource (T7).
    /// Returns <c>null</c> for an un-stamped runtime so the wizard falls back to
    /// its literal default palette. Kept separate from <see cref="LoadFromSelf"/>
    /// because the in-memory <see cref="WrapperBlob"/> record intentionally does
    /// not carry brand fields — they are a host-rendering concern, delivered via
    /// <see cref="SerializableWrapperBlob"/>.
    /// </summary>
    internal static InstallerBrandData? LoadBrandFromSelf()
    {
        var bytes = TryReadResource(BlobResourceName);
        if (bytes is null) return null;

        var json = System.Text.Encoding.UTF8.GetString(bytes);
        var s = System.Text.Json.JsonSerializer.Deserialize(
            json, WrapperBlobJsonContext.Default.SerializableWrapperBlob);
        if (s is null) return null;

        return new InstallerBrandData(
            Light: s.BrandTokensLight,
            Dark: s.BrandTokensDark,
            LogoBase64: s.LogoBase64,
            HeroBase64: s.HeroBase64,
            DisplayName: s.DisplayName,
            Publisher: s.Publisher,
            Version: s.Version);
    }

    private const string BlobResourceName = "SIGIL_BLOB_V1";
    private const string PayloadResourceName = "SIGIL_PAYLOAD_V1";

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

/// <summary>
/// Brand data extracted from the embedded blob for the wizard to render (T7):
/// the derived light/dark token maps, base64 logo/hero, and the ARP display
/// fields. Delivered inside the blob (decision 11) — no sidecar file. Public so
/// the Avalonia host (whose assembly name doesn't match the engine's
/// <c>InternalsVisibleTo</c>) can consume it via <see cref="InstallerBrandLoader"/>.
/// </summary>
public sealed record InstallerBrandData(
    IReadOnlyDictionary<string, string>? Light,
    IReadOnlyDictionary<string, string>? Dark,
    string? LogoBase64,
    string? HeroBase64,
    string? DisplayName,
    string? Publisher,
    string? Version);

/// <summary>
/// Public entry point for the host to read brand data from the stamped exe's
/// embedded blob without depending on the engine's internal types.
/// </summary>
public static class InstallerBrandLoader
{
    /// <summary>
    /// Read the brand data (derived palette + assets) from the running exe's
    /// <c>SIGIL_BLOB_V1</c> resource, or <c>null</c> for an un-stamped runtime.
    /// </summary>
    public static InstallerBrandData? LoadFromSelf() => WrapperBlob.LoadBrandFromSelf();
}
