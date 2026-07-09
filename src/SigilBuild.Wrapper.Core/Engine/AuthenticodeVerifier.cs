using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SigilBuild.Wrapper.Engine;

/// <summary>
/// AOT-safe Authenticode self-verification (T11 / decision 7). Wraps
/// <c>WinVerifyTrust</c> (wintrust.dll) via a source-generated
/// <see cref="LibraryImportAttribute"/> P/Invoke — no reflection, no runtime IL
/// stubs — so it publishes clean under <c>PublishAot=true</c> and
/// <c>TrimMode=full</c>. The runtime verifies its OWN embedded Authenticode
/// signature; the host gates the "Signed by {publisher}" trust line on the
/// result (see <see cref="InstallerTrustLoader"/>), so a tampered or re-stamped
/// exe whose signature no longer validates drops the line.
/// </summary>
/// <remarks>
/// Uses <c>WINTRUST_ACTION_GENERIC_VERIFY_V2</c> with <c>WTD_UI_NONE</c> (never
/// prompts) and <c>WTD_CHOICE_FILE</c> against the target file path. The two-call
/// VERIFY → CLOSE state protocol required by the API is honoured so the trust
/// provider frees its per-file state.
/// </remarks>
public static partial class AuthenticodeVerifier
{
    // WINTRUST_DATA.dwUIChoice — no UI, ever (headless installers / silent path).
    private const uint WTD_UI_NONE = 2;

    // WINTRUST_DATA.fdwRevocationChecks — skip online revocation; the self-check
    // asks "is this exe intact + validly signed", not "is the cert still live".
    private const uint WTD_REVOKE_NONE = 0;

    // WINTRUST_DATA.dwUnionChoice — verify a file (pFile → WINTRUST_FILE_INFO).
    private const uint WTD_CHOICE_FILE = 1;

    // WINTRUST_DATA.dwStateAction — open/verify then close the trust state.
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;

    // WINTRUST_DATA.dwProvFlags — suppress any SAFER UI cache prompts.
    private const uint WTD_SAFER_FLAG = 0x100;

    // {00AAC56B-CD44-11d0-8CC2-00C04FC295EE} — WINTRUST_ACTION_GENERIC_VERIFY_V2.
    private static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 =
        new(0x00AAC56B, 0xCD44, 0x11D0, 0x8C, 0xC2, 0x00, 0xC0, 0x4F, 0xC2, 0x95, 0xEE);

    /// <summary>
    /// Verify the RUNNING executable's own Authenticode signature. Returns
    /// <c>false</c> off Windows, when the process path is unavailable, or when the
    /// signature is absent / invalid / tampered.
    /// </summary>
    public static bool VerifySelf()
    {
        var self = Environment.ProcessPath;
        return !string.IsNullOrEmpty(self) && VerifyFile(self);
    }

    /// <summary>
    /// Verify a specific file's Authenticode signature. Returns <c>true</c> only
    /// when <c>WinVerifyTrust</c> reports full trust (<c>ERROR_SUCCESS</c>).
    /// Non-Windows hosts, a missing path, or any failure code → <c>false</c>.
    /// </summary>
    public static bool VerifyFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !OperatingSystem.IsWindows())
        {
            return false;
        }

        return VerifyFileWindows(filePath);
    }

    [SupportedOSPlatform("windows")]
    [ExcludeFromCodeCoverage(Justification =
        "Win32 WinVerifyTrust P/Invoke; exercised only by the gated Windows Authenticode integration test.")]
    private static unsafe bool VerifyFileWindows(string filePath)
    {
        var pathPtr = Marshal.StringToHGlobalUni(filePath);
        try
        {
            var fileInfo = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)sizeof(WINTRUST_FILE_INFO),
                pcwszFilePath = pathPtr,
                hFile = IntPtr.Zero,
                pgKnownSubject = IntPtr.Zero,
            };

            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)sizeof(WINTRUST_DATA),
                pPolicyCallbackData = IntPtr.Zero,
                pSIPClientData = IntPtr.Zero,
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = (IntPtr)(&fileInfo),
                dwStateAction = WTD_STATEACTION_VERIFY,
                hWVTStateData = IntPtr.Zero,
                pwszURLReference = IntPtr.Zero,
                dwProvFlags = WTD_SAFER_FLAG,
                dwUIContext = 0,
                pSignatureSettings = IntPtr.Zero,
            };

            var action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
            int result;
            try
            {
                // hwnd = 0 with WTD_UI_NONE guarantees a fully headless check.
                result = WinVerifyTrust(IntPtr.Zero, ref action, (IntPtr)(&data));
            }
            finally
            {
                // Second call frees the per-file trust state the VERIFY opened.
                data.dwStateAction = WTD_STATEACTION_CLOSE;
                _ = WinVerifyTrust(IntPtr.Zero, ref action, (IntPtr)(&data));
            }

            // ERROR_SUCCESS (0) is the only "fully trusted" verdict; every other
            // return (TRUST_E_NOSIGNATURE, TRUST_E_BAD_DIGEST from tampering,
            // CERT_E_* , etc.) means "do not show the trust line".
            return result == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(pathPtr);
        }
    }

    [SupportedOSPlatform("windows")]
    [LibraryImport("wintrust.dll", EntryPoint = "WinVerifyTrust")]
    private static partial int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, IntPtr pWVTData);

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;   // LPCWSTR
        public IntPtr hFile;
        public IntPtr pgKnownSubject;  // GUID*
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;           // union → WINTRUST_FILE_INFO*
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
}
