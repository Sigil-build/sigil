using System;
using System.Security.Cryptography;
using SigilBuild.Core.Diagnostics;

namespace SigilBuild.Wrapper.Update;

/// <summary>
/// Verifies a fetched channel manifest's detached ECDSA P-256 signature (P12,
/// T12.2) against <see cref="SigilBuild.Core.Manifest.UpdatesSection.SigningKey"/>,
/// per the encoding locked in on <see cref="ChannelManifest"/>'s remarks: a
/// base64 IEEE-P1363 (r‖s) signature fetched from <c>manifestUrl + ".sig"</c>,
/// checked with the BCL's <see cref="ECDsa"/> against a base64 X.509 SPKI DER
/// public key. This is a hard security gate for the update runtime (T12.3) —
/// every expected failure (bad signature, bad/missing key, malformed input,
/// wrong curve) is surfaced as <see cref="DiagnosticCodes.ChannelManifestSignatureInvalid"/>
/// (SIG0321) rather than an escaping exception, so a tampered or unsigned
/// channel manifest is rejected instead of crashing the caller into a state
/// where the failure mode is ambiguous.
/// </summary>
internal static class ChannelManifestVerifier
{
    /// <summary>
    /// Verify <paramref name="manifestBytes"/> (the exact bytes fetched from
    /// <c>manifestUrl</c>, unmodified) against <paramref name="signatureBase64"/>
    /// (the base64 body fetched from <c>manifestUrl + ".sig"</c>) using
    /// <paramref name="publicKeyBase64"/> (<c>updates.signingKey</c>). Never
    /// throws for expected failure modes — <see cref="CryptographicException"/>,
    /// <see cref="FormatException"/> (malformed base64), and
    /// <see cref="ArgumentException"/> (e.g. a non-P-256 key/signature shape)
    /// are all caught and mapped to a <see cref="ChannelManifestParseResult.Failed"/>
    /// carrying <see cref="DiagnosticCodes.ChannelManifestSignatureInvalid"/>
    /// (SIG0321). The failure message never includes the key or signature
    /// bytes/text (redaction-safe: those are secrets-adjacent material, not
    /// diagnostic content a log should carry).
    /// </summary>
    public static ChannelManifestVerifyResult Verify(
        byte[] manifestBytes,
        string? signatureBase64,
        string? publicKeyBase64)
    {
        ArgumentNullException.ThrowIfNull(manifestBytes);

        // An app that ships an `updates:` block without a signing key cannot be
        // trusted to auto-update — reject outright rather than treat "no key"
        // as "no verification needed".
        if (string.IsNullOrWhiteSpace(publicKeyBase64))
        {
            return Invalid("missing or empty signing key (updates.signingKey)");
        }

        if (string.IsNullOrWhiteSpace(signatureBase64))
        {
            return Invalid("missing or empty detached signature");
        }

        try
        {
            byte[] spki;
            byte[] signature;
            try
            {
                spki = Convert.FromBase64String(publicKeyBase64);
            }
            catch (FormatException)
            {
                return Invalid("signing key is not valid base64");
            }

            try
            {
                signature = Convert.FromBase64String(signatureBase64);
            }
            catch (FormatException)
            {
                return Invalid("detached signature is not valid base64");
            }

            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(spki, out _);

            // Pin the curve: a key that imports fine but isn't P-256 (e.g. a
            // P-384 SPKI) must not be accepted just because ImportSubjectPublicKeyInfo
            // didn't throw.
            if (ecdsa.KeySize != 256)
            {
                return Invalid("signing key is not a P-256 (secp256r1) key");
            }

            bool verified = ecdsa.VerifyData(manifestBytes, signature, HashAlgorithmName.SHA256);
            return verified
                ? ChannelManifestVerifyResult.Ok
                : Invalid("signature verification failed");
        }
        catch (CryptographicException)
        {
            return Invalid("signing key or signature could not be processed");
        }
        catch (ArgumentException)
        {
            return Invalid("signing key or signature has an invalid shape");
        }
    }

    private static ChannelManifestVerifyResult Invalid(string detail) =>
        ChannelManifestVerifyResult.Failed(
            DiagnosticCodes.ChannelManifestSignatureInvalid,
            $"Channel manifest signature verification failed: {detail}.");
}
