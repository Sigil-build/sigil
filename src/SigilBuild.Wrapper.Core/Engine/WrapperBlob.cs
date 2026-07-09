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
    IReadOnlyList<InstallStep> UpdateSteps,
    InstallScope Scope = InstallScope.Auto,
    // T8: the ENABLED built-in option components. The engine seeds
    // `option.<Name>` from these (default, unless a wizard checkbox or a
    // `/P<Name>=value` override supplies otherwise) so the auto-generated,
    // option-gated steps — and any hand-written `when: option.*` — evaluate.
    // Null/empty for an un-stamped runtime or a manifest declaring no options.
    IReadOnlyList<InstallerOptionComponent>? Options = null)
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
    /// Read the embedded payload bytes (<c>SIGIL_PAYLOAD_V2</c>) from the
    /// running executable. Returns an empty array when the resource isn't
    /// embedded, mirroring <see cref="LoadFromSelf"/>'s graceful-fallback
    /// behaviour for un-stamped runtimes.
    /// </summary>
    /// <remarks>
    /// The bytes are the deterministic zstd container produced by
    /// <c>SigilBuild.Wrapper.Codec.PayloadCodec</c>; <see cref="PayloadExtraction"/>
    /// decodes them into a temp dir for <c>payload://</c>-prefixed file_copy
    /// sources. This method only exposes the raw resource bytes.
    /// </remarks>
    public static byte[] LoadPayloadBytes()
    {
        return TryReadResource(PayloadResourceName) ?? Array.Empty<byte>();
    }

    /// <summary>
    /// Read the embedded native-dependency archive (<c>SIGIL_RUNTIME_V1</c>, T18)
    /// from the running exe. Returns <c>null</c> when the resource is absent — an
    /// un-stamped dev run whose Skia/ANGLE/HarfBuzz DLLs already sit beside the exe
    /// — so <see cref="NativeRuntimeBootstrap.EnsureNativeDependenciesLoadable"/>
    /// can no-op. The GUI bootstrap is the only caller.
    /// </summary>
    internal static byte[]? LoadRuntimeBytes()
    {
        return TryReadResource(NativeRuntimeBootstrap.RuntimeResourceName);
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

    /// <summary>
    /// Read the declared custom wizard screens (T9) from the embedded
    /// <c>SIGIL_BLOB_V1</c> resource. Returns an empty list for an un-stamped
    /// runtime (dev/preview) or a blob that declares no screens. Kept separate
    /// from <see cref="LoadFromSelf"/> because the in-memory <see cref="WrapperBlob"/>
    /// record does not carry screens — they are a host-rendering concern delivered
    /// via <see cref="SerializableWrapperBlob"/>.
    /// </summary>
    internal static System.Collections.Generic.IReadOnlyList<InstallerScreen> LoadScreensFromSelf()
    {
        var bytes = TryReadResource(BlobResourceName);
        if (bytes is null) return Array.Empty<InstallerScreen>();

        var json = System.Text.Encoding.UTF8.GetString(bytes);
        var s = System.Text.Json.JsonSerializer.Deserialize(
            json, WrapperBlobJsonContext.Default.SerializableWrapperBlob);
        if (s is null || s.Screens.Length == 0) return Array.Empty<InstallerScreen>();

        var result = new InstallerScreen[s.Screens.Length];
        for (var i = 0; i < s.Screens.Length; i++)
        {
            result[i] = SerializableInstallerScreen.ToInstallerScreen(s.Screens[i]);
        }
        return result;
    }

    /// <summary>
    /// Read the embedded license text (T14) from the running exe's
    /// <c>SIGIL_BLOB_V1</c> resource. Returns <c>null</c> for an un-stamped
    /// runtime, a blob with no license, or an empty license. Kept separate from
    /// <see cref="LoadFromSelf"/> because the in-memory <see cref="WrapperBlob"/>
    /// record does not carry license text — it is a host-rendering concern
    /// delivered via <see cref="SerializableWrapperBlob"/>.
    /// </summary>
    internal static string? LoadLicenseFromSelf()
    {
        var bytes = TryReadResource(BlobResourceName);
        if (bytes is null) return null;

        var json = System.Text.Encoding.UTF8.GetString(bytes);
        var s = System.Text.Json.JsonSerializer.Deserialize(
            json, WrapperBlobJsonContext.Default.SerializableWrapperBlob);
        return string.IsNullOrWhiteSpace(s?.LicenseText) ? null : s!.LicenseText;
    }

    private const string BlobResourceName = "SIGIL_BLOB_V1";

    // T6: the payload marker is bumped to SIGIL_PAYLOAD_V2 (deterministic zstd
    // container, see SigilBuild.Wrapper.Codec.PayloadCodec). Gating the reader on
    // the V2 name means a legacy V1 (Deflate zip) resource, or an un-stamped
    // runtime, both surface as an empty payload — never as a mis-decoded archive.
    private const string PayloadResourceName = "SIGIL_PAYLOAD_V2";

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

/// <summary>
/// Public entry point for the host to read the declared custom wizard screens
/// (T9) from the stamped exe's embedded blob without depending on the engine's
/// internal wire DTOs. Returns an empty list for an un-stamped runtime.
/// </summary>
public static class InstallerScreensLoader
{
    /// <summary>
    /// Read the declared custom wizard screens from the running exe's
    /// <c>SIGIL_BLOB_V1</c> resource, or an empty list for an un-stamped runtime.
    /// </summary>
    public static System.Collections.Generic.IReadOnlyList<InstallerScreen> LoadFromSelf()
        => WrapperBlob.LoadScreensFromSelf();
}

/// <summary>
/// Public entry point for the host to read the embedded license text (T14) from
/// the stamped exe's blob without depending on the engine's internal wire DTOs.
/// Returns <c>null</c> when no license is embedded (un-stamped runtime, or a
/// manifest with no <c>installer.license</c>) — the host then omits the License
/// screen entirely. Parallels <see cref="InstallerScreensLoader"/> /
/// <see cref="InstallerBrandLoader"/>.
/// </summary>
public static class InstallerLicenseLoader
{
    /// <summary>
    /// Read the embedded license text from the running exe's
    /// <c>SIGIL_BLOB_V1</c> resource, or <c>null</c> when none is present.
    /// </summary>
    public static string? LoadFromSelf() => WrapperBlob.LoadLicenseFromSelf();
}
