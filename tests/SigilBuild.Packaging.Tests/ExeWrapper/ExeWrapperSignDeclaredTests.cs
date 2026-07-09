using System.Text;
using System.Text.Json;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Packaging.ExeWrapper;
using SigilBuild.Wrapper.Json;
using Xunit;

namespace SigilBuild.Packaging.Tests.ExeWrapper;

/// <summary>
/// T11 / decision 7 pack → blob coverage:
/// <see cref="ExeWrapperPackager.BuildBlobBytes"/> sets
/// <see cref="SerializableWrapperBlob.SignDeclared"/> to <c>true</c> iff the
/// manifest declares a real <c>sign</c> block (provider ≠ <c>None</c>), and never
/// derives it from <c>App.publisher</c> alone. The runtime combines this flag with
/// a live <c>WinVerifyTrust</c> self-check to gate the trust line.
/// </summary>
public class ExeWrapperSignDeclaredTests
{
    private static SerializableWrapperBlob Deserialize(byte[] blob) =>
        JsonSerializer.Deserialize(
            Encoding.UTF8.GetString(blob),
            WrapperBlobJsonContext.Default.SerializableWrapperBlob)!;

    private static SigilManifest ManifestWithSign(SignSection? sign) =>
        new("v1.0",
            new AppSection("com.example.App", "Example", "1.0.0", "Example Inc.", null, null),
            new BuildSection("./out", null, null, true),
            null, sign, null, null,
            Installer: null,
            Location: SourceLocation.Unknown);

    [Fact]
    public void BuildBlobBytes_SignDeclaredTrue_WhenLocalProviderDeclared()
    {
        var sign = new SignSection(
            SignProvider.Local,
            new LocalSignConfig("cert.pfx", "PFX_PASSWORD", null),
            null);

        var blob = ExeWrapperPackager.BuildBlobBytes(ManifestWithSign(sign), string.Empty);
        Deserialize(blob).SignDeclared.Should().BeTrue();
    }

    [Fact]
    public void BuildBlobBytes_SignDeclaredTrue_WhenAzureProviderDeclared()
    {
        var sign = new SignSection(
            SignProvider.AzureTrustedSigning,
            null,
            new AzureTrustedSigningConfig(
                "https://eus.codesigning.azure.net", "acct", "profile",
                "AZ_TENANT", "AZ_CLIENT", "AZ_SECRET"));

        var blob = ExeWrapperPackager.BuildBlobBytes(ManifestWithSign(sign), string.Empty);
        Deserialize(blob).SignDeclared.Should().BeTrue();
    }

    [Fact]
    public void BuildBlobBytes_SignDeclaredFalse_WhenNoSignBlock()
    {
        // App.publisher IS set on the manifest, but with no `sign` block the flag
        // stays false — the trust line is never gated on the publisher name alone.
        var blob = ExeWrapperPackager.BuildBlobBytes(ManifestWithSign(null), string.Empty);
        Deserialize(blob).SignDeclared.Should().BeFalse();
    }

    [Fact]
    public void BuildBlobBytes_SignDeclaredFalse_WhenProviderNone()
    {
        var sign = new SignSection(SignProvider.None, null, null);
        var blob = ExeWrapperPackager.BuildBlobBytes(ManifestWithSign(sign), string.Empty);
        Deserialize(blob).SignDeclared.Should().BeFalse();
    }
}
