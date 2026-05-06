using System;
using System.IO;
using System.Security.Cryptography;

namespace SigilBuild.Packaging.Common;

public static class ManifestHasher
{
    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
