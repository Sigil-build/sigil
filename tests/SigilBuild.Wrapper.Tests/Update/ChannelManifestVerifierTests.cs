using System;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using SigilBuild.Core.Diagnostics;
using SigilBuild.Wrapper.Update;

namespace SigilBuild.Wrapper.Tests.Update;

/// <summary>
/// Tests for T12.2's <see cref="ChannelManifestVerifier"/>: detached ECDSA
/// P-256 signature verification of a fetched channel manifest against
/// <c>updates.signingKey</c>, per the locked encoding documented on
/// <see cref="ChannelManifest"/> (base64 IEEE-P1363 r‖s signature at
/// <c>manifestUrl + ".sig"</c>; base64 X.509 SPKI DER public key).
/// </summary>
public class ChannelManifestVerifierTests
{
    private static readonly byte[] ManifestBytes = Encoding.UTF8.GetBytes(
        """
        {
          "schemaVersion": 1,
          "version": "2.3.0",
          "packageUrl": "https://updates.example.com/acme/2.3.0/package.zip",
          "sha256": "b1946ac92492d2347c6235b4d2611184"
        }
        """);

    private static (string PublicKeyBase64, string SignatureBase64) SignWithNewKey(byte[] data)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki = ecdsa.ExportSubjectPublicKeyInfo();
        var sig = ecdsa.SignData(data, HashAlgorithmName.SHA256);
        return (Convert.ToBase64String(spki), Convert.ToBase64String(sig));
    }

    // ── Happy path ──────────────────────────────────────────────────────────

    [Fact]
    public void Valid_signature_from_the_matching_key_verifies_successfully()
    {
        var (publicKeyBase64, signatureBase64) = SignWithNewKey(ManifestBytes);

        var result = ChannelManifestVerifier.Verify(ManifestBytes, signatureBase64, publicKeyBase64);

        result.Success.Should().BeTrue();
        result.DiagnosticCode.Should().BeNull();
        result.Error.Should().BeNull();
    }

    // ── Tampered manifest ───────────────────────────────────────────────────

    [Fact]
    public void Tampered_manifest_bytes_fail_verification_with_SIG0321()
    {
        var (publicKeyBase64, signatureBase64) = SignWithNewKey(ManifestBytes);
        var tampered = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(ManifestBytes).Replace("2.3.0", "9.9.9"));

        var result = ChannelManifestVerifier.Verify(tampered, signatureBase64, publicKeyBase64);

        result.Success.Should().BeFalse();
        result.DiagnosticCode.Should().Be(DiagnosticCodes.ChannelManifestSignatureInvalid);
        result.DiagnosticCode.Should().Be("SIG0321");
        result.Error.Should().NotBeNullOrWhiteSpace();
    }

    // ── Wrong key ───────────────────────────────────────────────────────────

    [Fact]
    public void Signature_from_a_different_key_fails_verification_with_SIG0321()
    {
        var (_, signatureBase64) = SignWithNewKey(ManifestBytes);
        var (wrongPublicKeyBase64, _) = SignWithNewKey(ManifestBytes);

        var result = ChannelManifestVerifier.Verify(ManifestBytes, signatureBase64, wrongPublicKeyBase64);

        result.Success.Should().BeFalse();
        result.DiagnosticCode.Should().Be(DiagnosticCodes.ChannelManifestSignatureInvalid);
    }

    // ── Malformed public key base64 ─────────────────────────────────────────

    [Fact]
    public void Malformed_public_key_base64_fails_with_SIG0321_and_does_not_throw()
    {
        var (_, signatureBase64) = SignWithNewKey(ManifestBytes);

        var act = () => ChannelManifestVerifier.Verify(ManifestBytes, signatureBase64, "not-valid-base64!!!");

        var result = act.Should().NotThrow().Subject;
        result.Success.Should().BeFalse();
        result.DiagnosticCode.Should().Be(DiagnosticCodes.ChannelManifestSignatureInvalid);
    }

    // ── Malformed signature base64 ───────────────────────────────────────────

    [Fact]
    public void Malformed_signature_base64_fails_with_SIG0321_and_does_not_throw()
    {
        var (publicKeyBase64, _) = SignWithNewKey(ManifestBytes);

        var act = () => ChannelManifestVerifier.Verify(ManifestBytes, "not-valid-base64!!!", publicKeyBase64);

        var result = act.Should().NotThrow().Subject;
        result.Success.Should().BeFalse();
        result.DiagnosticCode.Should().Be(DiagnosticCodes.ChannelManifestSignatureInvalid);
    }

    // ── Empty / missing signingKey ───────────────────────────────────────────

    [Fact]
    public void Empty_signingKey_fails_with_SIG0321()
    {
        var (_, signatureBase64) = SignWithNewKey(ManifestBytes);

        var result = ChannelManifestVerifier.Verify(ManifestBytes, signatureBase64, string.Empty);

        result.Success.Should().BeFalse();
        result.DiagnosticCode.Should().Be(DiagnosticCodes.ChannelManifestSignatureInvalid);
    }

    [Fact]
    public void Null_signingKey_fails_with_SIG0321()
    {
        var (_, signatureBase64) = SignWithNewKey(ManifestBytes);

        var result = ChannelManifestVerifier.Verify(ManifestBytes, signatureBase64, null);

        result.Success.Should().BeFalse();
        result.DiagnosticCode.Should().Be(DiagnosticCodes.ChannelManifestSignatureInvalid);
    }

    [Fact]
    public void Whitespace_only_signingKey_fails_with_SIG0321()
    {
        var (_, signatureBase64) = SignWithNewKey(ManifestBytes);

        var result = ChannelManifestVerifier.Verify(ManifestBytes, signatureBase64, "   ");

        result.Success.Should().BeFalse();
        result.DiagnosticCode.Should().Be(DiagnosticCodes.ChannelManifestSignatureInvalid);
    }

    // ── Wrong curve key (P-384) ───────────────────────────────────────────────

    [Fact]
    public void Wrong_curve_P384_key_fails_with_SIG0321_and_does_not_throw()
    {
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var p384PublicKeyBase64 = Convert.ToBase64String(p384.ExportSubjectPublicKeyInfo());
        var p384Sig = Convert.ToBase64String(p384.SignData(ManifestBytes, HashAlgorithmName.SHA256));

        var act = () => ChannelManifestVerifier.Verify(ManifestBytes, p384Sig, p384PublicKeyBase64);

        var result = act.Should().NotThrow().Subject;
        result.Success.Should().BeFalse();
        result.DiagnosticCode.Should().Be(DiagnosticCodes.ChannelManifestSignatureInvalid);
    }

    // ── DER/Rfc3279-encoded signature is rejected (IEEE-P1363 is pinned) ────

    [Fact]
    public void Der_encoded_signature_is_rejected_because_IeeeP1363_is_pinned()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKeyBase64 = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
        var derSignatureBase64 = Convert.ToBase64String(
            ecdsa.SignData(ManifestBytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

        var result = ChannelManifestVerifier.Verify(ManifestBytes, derSignatureBase64, publicKeyBase64);

        result.Success.Should().BeFalse();
        result.DiagnosticCode.Should().Be(DiagnosticCodes.ChannelManifestSignatureInvalid);
    }
}
