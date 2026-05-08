using System.IO;
using FluentAssertions;
using NSec.Cryptography;
using SigilBuild.Signing.Local;
using Xunit;

namespace SigilBuild.Signing.Tests.Local;

public sealed class ZipManifestSignerTests
{
    [Fact]
    public void SignAndVerify_RoundTrip()
    {
        var key = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        var artifact = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".zip");
        File.WriteAllBytes(artifact, new byte[] { 0x50, 0x4B, 0x03, 0x04 });

        try
        {
            var sigPath = ZipManifestSigner.Sign(artifact, key);
            File.Exists(sigPath).Should().BeTrue();

            var publicKey = key.PublicKey;
            var verified = ZipManifestSigner.Verify(artifact, sigPath, publicKey);
            verified.Should().BeTrue();
        }
        finally { File.Delete(artifact); }
    }

    [Fact]
    public void Verify_TamperedArtifact_ReturnsFalse()
    {
        var key = Key.Create(SignatureAlgorithm.Ed25519);
        var artifact = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".zip");
        File.WriteAllBytes(artifact, new byte[] { 0x01, 0x02, 0x03 });

        try
        {
            var sig = ZipManifestSigner.Sign(artifact, key);
            File.WriteAllBytes(artifact, new byte[] { 0xFF, 0xFE, 0xFD });

            ZipManifestSigner.Verify(artifact, sig, key.PublicKey).Should().BeFalse();
        }
        finally { File.Delete(artifact); }
    }
}
