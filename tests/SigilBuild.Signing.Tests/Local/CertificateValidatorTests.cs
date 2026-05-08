using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using SigilBuild.Signing.Local;
using Xunit;

namespace SigilBuild.Signing.Tests.Local;

public sealed class CertificateValidatorTests
{
    private static X509Certificate2 BuildSelfSigned(DateTimeOffset notBefore, DateTimeOffset notAfter, bool codeSigning = true)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        if (codeSigning)
        {
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new("1.3.6.1.5.5.7.3.3") }, critical: true));
        }
        return req.CreateSelfSigned(notBefore, notAfter);
    }

    [Fact]
    public void Validate_FreshCodeSigningCert_ReturnsValid()
    {
        using var cert = BuildSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var result = CertificateValidator.Validate(cert);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_ExpiredCert_ReturnsInvalidWithReason()
    {
        using var cert = BuildSelfSigned(DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddDays(-1));
        var result = CertificateValidator.Validate(cert);
        result.IsValid.Should().BeFalse();
        result.Reason.Should().Contain("expired");
    }

    [Fact]
    public void Validate_NotYetValidCert_ReturnsInvalid()
    {
        using var cert = BuildSelfSigned(DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow.AddDays(30));
        var result = CertificateValidator.Validate(cert);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_NoCodeSigningEku_ReturnsInvalid()
    {
        using var cert = BuildSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30), codeSigning: false);
        var result = CertificateValidator.Validate(cert);
        result.IsValid.Should().BeFalse();
        result.Reason.Should().Contain("Code Signing");
    }
}
