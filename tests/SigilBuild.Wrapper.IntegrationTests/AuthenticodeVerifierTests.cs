using System;
using System.IO;
using FluentAssertions;
using SigilBuild.Wrapper.Engine;

namespace SigilBuild.Wrapper.IntegrationTests;

/// <summary>
/// Exercises the REAL <c>WinVerifyTrust</c> P/Invoke behind
/// <see cref="AuthenticodeVerifier"/> (T11 / decision 7) against files with a
/// known Authenticode state — no bespoke test cert required. Windows-only; on
/// other hosts (and when the reference file is absent) the tests soft-skip
/// (early <c>return</c>, reported Passed) mirroring the VM-gated install tests.
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

    [Fact]
    public void VerifyFile_returns_true_for_a_signed_system_binary()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(SignedSystemFile))
        {
            return; // soft-skip — see class remarks.
        }

        AuthenticodeVerifier.VerifyFile(SignedSystemFile).Should().BeTrue(
            "kernel32.dll is Authenticode/catalog-signed and must verify via WinVerifyTrust");
    }

    [Fact]
    public void VerifyFile_returns_false_for_an_unsigned_binary()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // soft-skip — see class remarks.
        }

        // A freshly written file has no signature: WinVerifyTrust reports
        // TRUST_E_NOSIGNATURE / TRUST_E_SUBJECT_FORM_UNKNOWN → not trusted.
        var tmp = Path.Combine(Path.GetTempPath(), "sigil-unsigned-" + Path.GetRandomFileName() + ".exe");
        File.WriteAllBytes(tmp, new byte[] { 0x4D, 0x5A, 0x00, 0x00 }); // "MZ" stub
        try
        {
            AuthenticodeVerifier.VerifyFile(tmp).Should().BeFalse();
        }
        finally
        {
            File.Delete(tmp);
        }
    }
}
