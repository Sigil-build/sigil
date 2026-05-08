using System.IO;
using NSec.Cryptography;

namespace SigilBuild.Signing.Local;

public static class ZipManifestSigner
{
    public static string Sign(string artifactPath, Key signingKey)
    {
        var bytes = File.ReadAllBytes(artifactPath);
        var sig = SignatureAlgorithm.Ed25519.Sign(signingKey, bytes);
        var sigPath = artifactPath + ".sig";
        File.WriteAllBytes(sigPath, sig);
        return sigPath;
    }

    public static bool Verify(string artifactPath, string signaturePath, PublicKey publicKey)
    {
        var bytes = File.ReadAllBytes(artifactPath);
        var sig = File.ReadAllBytes(signaturePath);
        return SignatureAlgorithm.Ed25519.Verify(publicKey, bytes, sig);
    }
}
