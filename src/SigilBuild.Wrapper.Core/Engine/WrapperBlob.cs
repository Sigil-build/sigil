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
    IReadOnlyList<InstallerOptionComponent>? Options = null,
    // T13: the manifest's App.Name and optional install-dir override. AppName is
    // the default install-dir base's <App.Name> segment and backs the {app.name}
    // token; InstallDir is the verbatim `installer.install_dir` template (may
    // reference {scope_root} / {app.*}), null when the manifest omits it so the
    // default `<scope root>\<App.Name>` applies. Both feed InstallDirResolver.
    string? AppName = null,
    string? InstallDir = null,
    // T10: the real Add/Remove Programs fields, sourced at pack time from
    // manifest.App.* (DisplayName ← App.Name, Publisher ← App.Publisher,
    // Version ← App.Version) and the packed size (EstimatedSizeBytes ← the
    // uncompressed payload footprint). Threaded into the ArpRegistration.Register
    // call by InstallSession.PersistCompletion — replacing the former
    // AppId / "1.0.0" / "Unknown" / 0 placeholders. Null/0 for an un-stamped
    // runtime, where PersistCompletion no-ops anyway.
    string? DisplayName = null,
    string? Publisher = null,
    string? Version = null,
    long EstimatedSizeBytes = 0,
    // P1 (gap G1): declarative variables from installer.vars, in manifest
    // declaration order. The engine evaluates each once at session start (in
    // dependency order — see InstallerVarGraph) and seeds var.<Name>. Null/empty
    // for a manifest declaring no vars.
    IReadOnlyList<InstallerVar>? Vars = null,
    // P2 (gap G2): lifecycle hooks that run OUTSIDE the rollback journal, around
    // the transactional body. Governed only by each step's on_failure; no rollback.
    IReadOnlyList<InstallStep>? HookPreInstall = null,
    IReadOnlyList<InstallStep>? HookPostInstall = null,
    IReadOnlyList<InstallStep>? HookPreUninstall = null,
    IReadOnlyList<InstallStep>? HookPostUninstall = null,
    // P2 (gap G4): the Done-screen "Launch <App>" target (path + args). Null when
    // the manifest declares no installer.run_after_install.
    string? RunAfterInstallPath = null,
    IReadOnlyList<string>? RunAfterInstallArgs = null,
    // P5 (gap G6): first-class prerequisite units from installer.prerequisites, in
    // declaration order. Run before the journaled body (detect → install → re-detect).
    // Null/empty for a manifest declaring no prerequisites.
    IReadOnlyList<InstallerPrerequisite>? Prerequisites = null)
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
            Version: s.Version,
            SignDeclared: s.SignDeclared);
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
    string? Version,
    // T11 / decision 7: whether the artifact declared a verified `sign` block.
    // Combined with WinVerifyTrust(self) to gate the trust line (see
    // InstallerTrustLoader). Appended last to keep the record backward-compatible.
    bool SignDeclared = false);

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

/// <summary>
/// Public entry point for the host to resolve the verified-signature-gated trust
/// line (T11 / decision 7). The "Signed by {publisher}" line renders ONLY when the
/// manifest declared a <c>sign</c> block (<c>SignDeclared</c>, carried in the blob)
/// AND the running exe's Authenticode signature verifies via <see cref="AuthenticodeVerifier"/>
/// — so an unsigned artifact, or a signed-then-tampered / re-stamped one whose
/// signature no longer validates, shows no trust line. The neutral publisher name
/// (rail) is always shown; the trust line is the additional, security-bearing label.
/// Parallels <see cref="InstallerBrandLoader"/> — the Avalonia host consumes it
/// without depending on the engine's internal types.
/// </summary>
public static class InstallerTrustLoader
{
    /// <summary>
    /// The pure trust-line decision, factored out so the three-case gating logic
    /// (signed → line; unsigned → none; signed-then-tampered → none) is unit-testable
    /// by faking <paramref name="signatureValid"/> without a real Authenticode cert.
    /// Returns the trust-line label when <paramref name="signDeclared"/> AND
    /// <paramref name="signatureValid"/>, otherwise <c>null</c> (no trust line).
    /// </summary>
    public static string? ResolveTrustLine(bool signDeclared, bool signatureValid, string? publisher)
        => signDeclared && signatureValid
            ? (string.IsNullOrWhiteSpace(publisher) ? "Signed" : $"Signed by {publisher}")
            : null;

    /// <summary>
    /// Full self-check: reads <c>SignDeclared</c> + publisher from the running exe's
    /// embedded blob, verifies the exe's OWN Authenticode signature via
    /// <see cref="AuthenticodeVerifier.VerifySelf"/>, and resolves the trust line.
    /// Returns <c>null</c> for an un-stamped runtime, an unsigned artifact, or a
    /// signature that fails to verify. The GUI host calls this once at startup.
    /// </summary>
    public static string? ResolveFromSelf()
    {
        var brand = WrapperBlob.LoadBrandFromSelf();
        var signDeclared = brand?.SignDeclared ?? false;
        // Short-circuit the P/Invoke when the artifact never declared signing —
        // no point verifying a signature that was never intended to exist.
        var valid = signDeclared && AuthenticodeVerifier.VerifySelf();
        return ResolveTrustLine(signDeclared, valid, brand?.Publisher);
    }
}
