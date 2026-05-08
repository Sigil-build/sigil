using System;
using System.Linq;
using System.Security.Cryptography.X509Certificates;

namespace SigilBuild.Signing.Local;

public sealed record CertificateValidationResult(bool IsValid, string Reason);

public static class CertificateValidator
{
    private const string CodeSigningOid = "1.3.6.1.5.5.7.3.3";

    public static CertificateValidationResult Validate(X509Certificate2 cert)
    {
        var now = DateTime.UtcNow;
        if (cert.NotBefore.ToUniversalTime() > now)
            return new(false, $"certificate not yet valid (NotBefore={cert.NotBefore:o})");
        if (cert.NotAfter.ToUniversalTime() < now)
            return new(false, $"certificate expired (NotAfter={cert.NotAfter:o})");

        var hasEku = cert.Extensions.OfType<X509EnhancedKeyUsageExtension>()
            .SelectMany(e => e.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>())
            .Any(o => o.Value == CodeSigningOid);
        if (!hasEku)
            return new(false, "certificate has no Code Signing (1.3.6.1.5.5.7.3.3) EKU");

        return new(true, "ok");
    }
}
