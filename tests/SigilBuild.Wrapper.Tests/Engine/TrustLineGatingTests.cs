using FluentAssertions;
using SigilBuild.Wrapper.Engine;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Engine;

/// <summary>
/// T11 / decision 7: the verified-signature-gated trust line. The pure decision
/// (<see cref="InstallerTrustLoader.ResolveTrustLine"/>) is unit-testable by faking
/// the <c>WinVerifyTrust</c> result, covering the three acceptance cases without a
/// real Authenticode certificate:
/// (1) signed + verified → the trust line shows;
/// (2) unsigned (SignDeclared false) → no trust line;
/// (3) signed-then-tampered / re-stamped (SignDeclared true, verify false) → no line.
/// The real P/Invoke is exercised separately (gated) in the integration tests.
/// </summary>
public class TrustLineGatingTests
{
    [Fact]
    public void Case1_signed_and_verified_shows_trust_line()
    {
        InstallerTrustLoader.ResolveTrustLine(signDeclared: true, signatureValid: true, publisher: "Acme, Inc.")
            .Should().Be("Signed by Acme, Inc.");
    }

    [Fact]
    public void Case2_unsigned_shows_no_trust_line()
    {
        // SignDeclared false: even a (spuriously) valid signature must not surface a
        // trust line — the artifact never declared signing.
        InstallerTrustLoader.ResolveTrustLine(signDeclared: false, signatureValid: true, publisher: "Acme, Inc.")
            .Should().BeNull();
        InstallerTrustLoader.ResolveTrustLine(signDeclared: false, signatureValid: false, publisher: "Acme, Inc.")
            .Should().BeNull();
    }

    [Fact]
    public void Case3_signed_then_tampered_shows_no_trust_line()
    {
        // SignDeclared true but the signature no longer verifies (tampered/re-stamped):
        // the trust line drops.
        InstallerTrustLoader.ResolveTrustLine(signDeclared: true, signatureValid: false, publisher: "Acme, Inc.")
            .Should().BeNull();
    }

    [Fact]
    public void Verified_without_publisher_falls_back_to_bare_signed_label()
    {
        InstallerTrustLoader.ResolveTrustLine(signDeclared: true, signatureValid: true, publisher: null)
            .Should().Be("Signed");
        InstallerTrustLoader.ResolveTrustLine(signDeclared: true, signatureValid: true, publisher: "   ")
            .Should().Be("Signed");
    }

    [Fact]
    public void VerifyFile_returns_false_for_missing_or_empty_path()
    {
        AuthenticodeVerifier.VerifyFile(string.Empty).Should().BeFalse();
        AuthenticodeVerifier.VerifyFile("   ").Should().BeFalse();
    }

    [Fact]
    public void VerifyFile_returns_false_for_unsigned_managed_assembly()
    {
        // The running test assembly is not Authenticode-signed, so a real
        // WinVerifyTrust call (on Windows) must report "not trusted"; off Windows
        // the guarded P/Invoke short-circuits to false. Either way: no false-positive
        // trust line for an unsigned binary.
        var self = typeof(TrustLineGatingTests).Assembly.Location;
        AuthenticodeVerifier.VerifyFile(self).Should().BeFalse();
    }
}
