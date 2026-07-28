using System;
using System.IO;
using FluentAssertions;
using SigilBuild.Wrapper.Engine;

namespace SigilBuild.Wrapper.IntegrationTests;

/// <summary>
/// Exercises the REAL <c>WinVerifyTrust</c> P/Invoke behind
/// <see cref="AuthenticodeVerifier"/> (T11 / decision 7) against files with a
/// known Authenticode state — no bespoke test cert required. Windows-only; on
/// other hosts (and when the reference file is absent) the tests report a genuine
/// Skipped result (via <see cref="AuthenticodeFactAttribute"/> /
/// <see cref="AuthenticodeReferenceFileFactAttribute"/>, register row R6) mirroring
/// the VM-gated install tests.
/// The pure trust-line gating decision is covered in the unit test project; this
/// proves the native call itself distinguishes a validly-signed binary from an
/// unsigned one.
/// </summary>
public class AuthenticodeVerifierTests
{
    // A Microsoft-signed OS binary present on every Windows install — a stable,
    // genuinely Authenticode/catalog-signed reference file.
    private static string SignedSystemFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "kernel32.dll");

    [AuthenticodeReferenceFileFact]
    public void VerifyFile_returns_true_for_a_signed_system_binary()
    {
        // Register row R17 switched revocation checking on (WTD_REVOKE_WHOLECHAIN), which
        // means this file's verdict now depends on whether a CRL/OCSP responder is
        // reachable. Asserting BeTrue would make this test pass on a networked host and
        // fail on an air-gapped one for a reason that has nothing to do with the
        // signature — so it asserts what is actually invariant: a genuinely signed
        // Microsoft binary is never reported unsigned, invalid or revoked, whatever the
        // host's connectivity.
        var status = AuthenticodeVerifier.VerifyFileStatus(SignedSystemFile);

        status.Should().BeOneOf(
            new[] { AuthenticodeStatus.Trusted, AuthenticodeStatus.RevocationUnavailable },
            "kernel32.dll is Authenticode/catalog-signed; only its revocation reachability may vary");
    }

    [AuthenticodeFact]
    public void VerifyFile_returns_false_for_an_unsigned_binary()
    {
        // A freshly written file has no signature: WinVerifyTrust reports
        // TRUST_E_NOSIGNATURE / TRUST_E_SUBJECT_FORM_UNKNOWN → not trusted.
        var tmp = Path.Combine(Path.GetTempPath(), "sigil-unsigned-" + Path.GetRandomFileName() + ".exe");
        File.WriteAllBytes(tmp, new byte[] { 0x4D, 0x5A, 0x00, 0x00 }); // "MZ" stub
        try
        {
            AuthenticodeVerifier.VerifyFile(tmp).Should().BeFalse();

            // R11's gate needs more than "not trusted": an absent signature is waivable
            // per prerequisite, a revoked one never is, so the real API must separate them
            // and not just report a bool. Connectivity cannot change this verdict — there
            // is no certificate here to check anything about.
            AuthenticodeVerifier.VerifyFileStatus(tmp).Should().Be(AuthenticodeStatus.NoSignature);
        }
        finally
        {
            File.Delete(tmp);
        }
    }
}
