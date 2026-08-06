using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SigilBuild.Wrapper.Engine;

/// <summary>
/// What <c>WinVerifyTrust</c> concluded about a file. Three-valued on purpose
/// (register row R17): with revocation checking switched on, "could not establish
/// whether this certificate is still valid" is a real and common answer — an
/// air-gapped machine, a blocked CRL distribution point, a captive portal — and it is
/// neither <see cref="Trusted"/> nor <see cref="Revoked"/>. Collapsing it into either
/// one is the bug: into the first and an offline box reads a revoked publisher as
/// good; into the second and every offline install reads its own genuine publisher as
/// forged.
/// </summary>
public enum AuthenticodeStatus
{
    /// <summary>
    /// No verdict was sought: not Windows, or no path was given. Distinct from
    /// <see cref="Invalid"/> — nothing was examined, so nothing was found wanting.
    /// </summary>
    NotEvaluated,

    /// <summary>
    /// <c>ERROR_SUCCESS</c>: signed, intact, chaining to a root this machine trusts,
    /// and not revoked. See the <see cref="AuthenticodeVerifier"/> remarks for what
    /// this does and does not say about publisher identity.
    /// </summary>
    Trusted,

    /// <summary>
    /// The signature and chain check out, but the revocation state could not be
    /// obtained (<c>CRYPT_E_REVOCATION_OFFLINE</c>, <c>CRYPT_E_NO_REVOCATION_CHECK</c>,
    /// <c>CERT_E_REVOCATION_FAILURE</c>). Renders as its own trust state and is not
    /// treated as a refusal — see <see cref="DownloadedBinaryTrust"/>.
    /// </summary>
    RevocationUnavailable,

    /// <summary>
    /// The certificate is revoked, or explicitly distrusted
    /// (<c>CERT_E_REVOKED</c>, <c>TRUST_E_EXPLICIT_DISTRUST</c>). Someone with
    /// authority has said this key is bad. Never opted out of.
    /// </summary>
    Revoked,

    /// <summary>
    /// The file carries no Authenticode signature at all
    /// (<c>TRUST_E_NOSIGNATURE</c> and friends). Common and legitimate for
    /// redistributables, which is why the opt-out exists.
    /// </summary>
    NoSignature,

    /// <summary>
    /// A signature is present but does not establish trust: tampered
    /// (<c>TRUST_E_BAD_DIGEST</c>), untrusted or broken chain, expired, wrong usage.
    /// </summary>
    Invalid,
}

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
/// <para>
/// Uses <c>WINTRUST_ACTION_GENERIC_VERIFY_V2</c> with <c>WTD_UI_NONE</c> (never
/// prompts) and <c>WTD_CHOICE_FILE</c> against the target file path. The two-call
/// VERIFY → CLOSE state protocol required by the API is honoured so the trust
/// provider frees its per-file state.
/// </para>
/// <para>
/// <b>What a <see cref="AuthenticodeStatus.Trusted"/> verdict does NOT say.</b> It says
/// the file is intact and its signature chains to a root <em>this machine</em> trusts —
/// which includes the per-user <c>Root</c> store, writable without any privilege. So a
/// non-administrator can mint a certificate, install its root for their own account, sign
/// anything with it, and have <c>WinVerifyTrust</c> return <c>ERROR_SUCCESS</c> for it.
/// "Authenticode-valid" is therefore an integrity statement, not a publisher-identity
/// one, and nothing in this type or its callers should be read as pinning a publisher.
/// Closing that needs an authenticated identity to pin <em>against</em> — a subject or
/// public-key hash carried in the pack-time manifest — plus chain inspection this file
/// does not do. Deliberately out of scope here; see the lane report.
/// </para>
/// </remarks>
public static partial class AuthenticodeVerifier
{
    // WINTRUST_DATA.dwUIChoice — no UI, ever (headless installers / silent path).
    private const uint WTD_UI_NONE = 2;

    // WINTRUST_DATA.fdwRevocationChecks — check the WHOLE chain's revocation state
    // (register row R17; was WTD_REVOKE_NONE = 0). A revoked publisher certificate is
    // exactly the case the trust line and the launch gate exist to catch, and skipping
    // the check made both of them assert something they had not looked at. This does
    // reach the network for CRL / OCSP retrieval when the answer is not already in the
    // local cache; that is accepted, and an unreachable responder surfaces as the
    // distinct AuthenticodeStatus.RevocationUnavailable rather than as trust or as
    // forgery. Cache-only retrieval was considered and rejected: it would report
    // "unavailable" for most first-time checks and quietly hollow the check out.
    private const uint WTD_REVOKE_WHOLECHAIN = 1;

    // WINTRUST_DATA.dwUnionChoice — verify a file (pFile → WINTRUST_FILE_INFO).
    private const uint WTD_CHOICE_FILE = 1;

    // WINTRUST_DATA.dwStateAction — open/verify then close the trust state.
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;

    // WINTRUST_DATA.dwProvFlags — suppress any SAFER UI cache prompts.
    private const uint WTD_SAFER_FLAG = 0x100;

    // WINTRUST_DATA.dwProvFlags — revoke every certificate in the chain EXCEPT the
    // root. A trust anchor is not revocable by a CRL it would have to sign itself, and
    // most roots publish no distribution point at all, so including it turns every
    // check into a spurious "revocation unavailable".
    private const uint WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT = 0x80;

    // WinVerifyTrust HRESULTs this type distinguishes. Anything not listed collapses
    // into AuthenticodeStatus.Invalid — the fail-closed bucket.
    private const int ERROR_SUCCESS = 0;
    private const int TRUST_E_PROVIDER_UNKNOWN = unchecked((int)0x800B0001);
    private const int TRUST_E_SUBJECT_FORM_UNKNOWN = unchecked((int)0x800B0003);
    private const int TRUST_E_NOSIGNATURE = unchecked((int)0x800B0100);
    private const int CERT_E_REVOKED = unchecked((int)0x800B010C);
    private const int CERT_E_REVOCATION_FAILURE = unchecked((int)0x800B010E);
    private const int TRUST_E_EXPLICIT_DISTRUST = unchecked((int)0x800B0111);
    private const int CRYPT_E_NO_REVOCATION_CHECK = unchecked((int)0x80092012);
    private const int CRYPT_E_REVOCATION_OFFLINE = unchecked((int)0x80092013);

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
    /// Verify the RUNNING executable's own signature and return the full verdict,
    /// including <see cref="AuthenticodeStatus.RevocationUnavailable"/>.
    /// </summary>
    public static AuthenticodeStatus VerifySelfStatus()
    {
        var self = Environment.ProcessPath;
        return string.IsNullOrEmpty(self) ? AuthenticodeStatus.NotEvaluated : VerifyFileStatus(self);
    }

    /// <summary>
    /// Verify a specific file's Authenticode signature. Returns <c>true</c> only
    /// when <c>WinVerifyTrust</c> reports full trust (<c>ERROR_SUCCESS</c>).
    /// Non-Windows hosts, a missing path, or any failure code → <c>false</c>.
    /// </summary>
    /// <remarks>
    /// The two-valued view. <see cref="AuthenticodeStatus.RevocationUnavailable"/>
    /// collapses to <c>false</c> here, which is the safe direction for a caller that
    /// only wants "is this fully trusted" — but a caller deciding whether to REFUSE
    /// something must use <see cref="VerifyFileStatus"/> instead, or an offline machine
    /// becomes indistinguishable from a forgery.
    /// </remarks>
    public static bool VerifyFile(string filePath) =>
        VerifyFileStatus(filePath) == AuthenticodeStatus.Trusted;

    /// <summary>
    /// Verify a specific file's Authenticode signature and return the full verdict.
    /// Non-Windows hosts and an empty path yield
    /// <see cref="AuthenticodeStatus.NotEvaluated"/> — nothing was examined.
    /// </summary>
    public static AuthenticodeStatus VerifyFileStatus(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !OperatingSystem.IsWindows())
        {
            return AuthenticodeStatus.NotEvaluated;
        }

        return Classify(VerifyFileWindows(filePath));
    }

    /// <summary>
    /// Map a <c>WinVerifyTrust</c> HRESULT onto <see cref="AuthenticodeStatus"/>.
    /// Factored out of the P/Invoke so the classification — which is the part that
    /// carries the security decision — is unit-testable on any host, including the
    /// codes no fixture on a developer box can produce.
    /// </summary>
    internal static AuthenticodeStatus Classify(int hresult) => hresult switch
    {
        ERROR_SUCCESS => AuthenticodeStatus.Trusted,

        // Someone with authority has said this key is bad.
        CERT_E_REVOKED or TRUST_E_EXPLICIT_DISTRUST => AuthenticodeStatus.Revoked,

        // The chain checked out but its revocation state could not be established.
        CRYPT_E_REVOCATION_OFFLINE or CRYPT_E_NO_REVOCATION_CHECK or CERT_E_REVOCATION_FAILURE
            => AuthenticodeStatus.RevocationUnavailable,

        // No signature to speak of. TRUST_E_SUBJECT_FORM_UNKNOWN / TRUST_E_PROVIDER_UNKNOWN
        // are what an unsigned or non-PE subject actually comes back as in practice.
        TRUST_E_NOSIGNATURE or TRUST_E_SUBJECT_FORM_UNKNOWN or TRUST_E_PROVIDER_UNKNOWN
            => AuthenticodeStatus.NoSignature,

        // Everything else — tampered digests, untrusted or broken chains, expiry,
        // wrong EKU — is the fail-closed bucket. New codes land here by default,
        // which is the correct direction for an unknown trust failure.
        _ => AuthenticodeStatus.Invalid,
    };

    [SupportedOSPlatform("windows")]
    [ExcludeFromCodeCoverage(Justification =
        "Win32 WinVerifyTrust P/Invoke; exercised only by the gated Windows Authenticode integration test.")]
    private static unsafe int VerifyFileWindows(string filePath)
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
                fdwRevocationChecks = WTD_REVOKE_WHOLECHAIN,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = (IntPtr)(&fileInfo),
                dwStateAction = WTD_STATEACTION_VERIFY,
                hWVTStateData = IntPtr.Zero,
                pwszURLReference = IntPtr.Zero,
                dwProvFlags = WTD_SAFER_FLAG | WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT,
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

            // The raw HRESULT, classified by Classify. Returning a bool here was the
            // shape that made "offline" and "forged" the same answer.
            return result;
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
