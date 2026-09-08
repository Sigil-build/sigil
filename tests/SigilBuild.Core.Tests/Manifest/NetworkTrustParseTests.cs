using System.Linq;
using FluentAssertions;
using SigilBuild.Core.Configuration;
using SigilBuild.Core.Diagnostics;
using SigilBuild.Core.Manifest;
using Xunit;

namespace SigilBuild.Core.Tests.Manifest;

/// <summary>
/// Pack-time network-trust validation: register rows R8 (<c>parameters.*.source.url</c>
/// must be HTTPS, SIG0323), R14 (<c>updates.manifestUrl</c> must be HTTPS, SIG0324),
/// R30 (<c>updates.signingKey</c> must be a base64 P-256 SPKI, SIG0325) and R45
/// (<c>installer.require_signed_downloads</c> must name a known policy, SIG0326).
/// </summary>
/// <remarks>
/// <para>
/// <b>These are written to compile and FAIL on the parent commit.</b> Every assertion
/// is against a diagnostic code as a string literal and against
/// <see cref="ManifestParser.Parse"/>, which both exist at the parent — no new type,
/// constant, or enum is named, so each test can be dropped onto <c>b62de86</c> as-is
/// and will fail there because the diagnostic is simply never produced.
/// </para>
/// <para>
/// <b>Each row also carries a positive control.</b> Stage 1 produced eleven
/// over-refusals; a tightening with no test for what it must still ACCEPT is how they
/// got in.
/// </para>
/// </remarks>
public class NetworkTrustParseTests
{
    /// <summary>A real base64 X.509 SPKI DER of an ECDSA P-256 public key.</summary>
    private const string ValidP256Spki =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEM6pwH5xM2+mhJt1IQ29ejc6kQVnvyPXhUGoX9nUttZmX" +
        "Ahvgbx9xTMcLoNEGpK3zdYmQRTR8h/ftYEBZuNznhw==";

    private static string WithParameterSource(string url) =>
        "spec: v1.0\n" +
        "app: { id: com.example.App, name: App, version: 0.1.0, publisher: P }\n" +
        "build: { source: ./out }\n" +
        "parameters:\n" +
        "  edition:\n" +
        "    type: string\n" +
        "    source:\n" +
        $"      url: \"{url}\"\n" +
        "      items_path: \"items\"\n" +
        "      value_property: \"id\"\n" +
        "      label_property: \"name\"\n";

    private static string WithUpdates(string body) =>
        "spec: v1.0\n" +
        "app: { id: com.example.App, name: App, version: 0.1.0, publisher: P }\n" +
        "build: { source: ./out }\n" +
        "updates:\n" + body;

    private static string WithInstaller(string body) =>
        "spec: v1.0\n" +
        "app: { id: com.example.App, name: App, version: 0.1.0, publisher: P }\n" +
        "build: { source: ./out }\n" +
        "installer:\n" + body;

    // ── R8: parameters.*.source.url ───────────────────────────────────────────

    [Theory]
    [InlineData("http://example.com/editions.json")]
    [InlineData("ftp://example.com/editions.json")]
    [InlineData("file:///C:/editions.json")]
    public void Parameter_source_url_must_be_https(string insecureUrl)
    {
        var result = ManifestParser.Parse(WithParameterSource(insecureUrl), "sigil.yaml");

        result.Diagnostics.Should().Contain(
            d => d.Severity == DiagnosticSeverity.Error && d.Code == "SIG0323",
            "a parameter `source.url` that is not https:// must be refused at pack time — the values " +
            "it fetches become parameter values, and parameter values are substituted into install " +
            "steps (paths, registry coordinates, arguments) that execute elevated");
    }

    [Fact]
    public void Parameter_source_url_over_https_is_still_accepted()
    {
        var result = ManifestParser.Parse(
            WithParameterSource("https://example.com/editions.json"), "sigil.yaml");

        result.Diagnostics.Should().NotContain(
            d => d.Severity == DiagnosticSeverity.Error,
            "the R8 fix must refuse cleartext, not dynamic parameter sources as a category");
        result.Manifest!.Parameters!["edition"].Source!.Url
            .Should().Be("https://example.com/editions.json");
    }

    [Fact]
    public void A_parameter_with_no_source_block_is_unaffected()
    {
        var yaml =
            "spec: v1.0\n" +
            "app: { id: com.example.App, name: App, version: 0.1.0, publisher: P }\n" +
            "build: { source: ./out }\n" +
            "parameters:\n" +
            "  edition:\n" +
            "    type: string\n" +
            "    default: pro\n";

        var result = ManifestParser.Parse(yaml, "sigil.yaml");

        result.Diagnostics.Should().NotContain(d => d.Code == "SIG0323");
    }

    // ── R14: updates.manifestUrl ──────────────────────────────────────────────

    [Theory]
    [InlineData("http://updates.example.com/manifest.json")]
    [InlineData("ftp://updates.example.com/manifest.json")]
    public void Updates_manifestUrl_must_be_https(string insecureUrl)
    {
        var result = ManifestParser.Parse(
            WithUpdates($"  manifestUrl: \"{insecureUrl}\"\n  signingKey: {ValidP256Spki}\n"),
            "sigil.yaml");

        result.Diagnostics.Should().Contain(
            d => d.Severity == DiagnosticSeverity.Error && d.Code == "SIG0324",
            "the schema's own description has always called this an HTTPS URL while constraining " +
            "only `format: uri`; the detached-signature URL is this string + '.sig', so a cleartext " +
            "manifestUrl drags the signature fetch onto cleartext with it");
    }

    [Fact]
    public void Updates_manifestUrl_over_https_is_still_accepted()
    {
        var result = ManifestParser.Parse(
            WithUpdates($"  manifestUrl: \"https://updates.example.com/manifest.json\"\n  signingKey: {ValidP256Spki}\n"),
            "sigil.yaml");

        result.Diagnostics.Should().NotContain(d => d.Severity == DiagnosticSeverity.Error);
        result.Manifest!.Updates!.ManifestUrl.Should().Be("https://updates.example.com/manifest.json");
    }

    // ── R30: updates.signingKey ───────────────────────────────────────────────

    [Theory]
    // The exact value `sigil init --template full` used to emit: a private-key FILE
    // PATH, naming the wrong algorithm.
    [InlineData("./keys/update-signing.ed25519")]
    [InlineData("./key.pem")]
    [InlineData("C:\\\\keys\\\\update.pem")]
    // Valid base64, but not an SPKI at all.
    [InlineData("aGVsbG8gd29ybGQ=")]
    // Not base64.
    [InlineData("not base64 at all!!!")]
    public void Updates_signingKey_must_be_a_base64_p256_spki(string badKey)
    {
        var result = ManifestParser.Parse(
            WithUpdates($"  manifestUrl: \"https://updates.example.com/manifest.json\"\n  signingKey: \"{badKey}\"\n"),
            "sigil.yaml");

        result.Diagnostics.Should().Contain(
            d => d.Severity == DiagnosticSeverity.Error && d.Code == "SIG0325",
            "updates.signingKey is the update runtime's trust anchor and the schema has always said " +
            "it is a base64 SPKI public key, never a private key and never a file path — packing a " +
            "file path produces an installer whose every update attempt dies at SIG0321 on every " +
            "machine it was installed on");
    }

    [Fact]
    public void A_real_base64_p256_spki_signingKey_is_accepted()
    {
        var result = ManifestParser.Parse(
            WithUpdates($"  manifestUrl: \"https://updates.example.com/manifest.json\"\n  signingKey: {ValidP256Spki}\n"),
            "sigil.yaml");

        result.Diagnostics.Should().NotContain(
            d => d.Severity == DiagnosticSeverity.Error,
            "the R30 fix must accept the documented shape — otherwise it refuses every correct manifest");
        result.Manifest!.Updates!.SigningKey.Should().Be(ValidP256Spki);
    }

    [Fact]
    public void An_updates_block_with_no_signingKey_is_unaffected()
    {
        // signingKey is optional at parse time (the update runtime refuses to act without
        // it, SIG0321). R30 validates the SHAPE of a declared key; it does not make the
        // field required, which would break every manifest that does not use updates.
        var result = ManifestParser.Parse(
            WithUpdates("  channel: stable\n"), "sigil.yaml");

        result.Diagnostics.Should().NotContain(d => d.Code == "SIG0325");
    }

    // ── R45: installer.require_signed_downloads ───────────────────────────────

    [Theory]
    [InlineData("yes")]
    [InlineData("true")]
    [InlineData("signdeclared")]
    [InlineData("always_verified_revokation")] // plausible typo
    public void An_unknown_require_signed_downloads_value_is_refused(string badValue)
    {
        var result = ManifestParser.Parse(
            WithInstaller($"  require_signed_downloads: {badValue}\n"), "sigil.yaml");

        result.Diagnostics.Should().Contain(
            d => d.Severity == DiagnosticSeverity.Error && d.Code == "SIG0326",
            "this setting governs whether a binary pulled off the network is checked before it is " +
            "run elevated; silently ignoring a typo in it would disarm the gate exactly the way " +
            "R45 exists to stop");
    }

    [Theory]
    [InlineData("sign_declared")]
    [InlineData("always")]
    [InlineData("always_verified_revocation")]
    public void Every_declared_require_signed_downloads_policy_is_accepted(string value)
    {
        var result = ManifestParser.Parse(
            WithInstaller($"  require_signed_downloads: {value}\n"), "sigil.yaml");

        result.Diagnostics.Should().NotContain(d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void An_installer_block_without_the_policy_parses_clean()
    {
        // R45 must be additive: the overwhelming majority of manifests will never name it.
        var result = ManifestParser.Parse(WithInstaller("  install_dir: \"{scope_root}/App\"\n"), "sigil.yaml");

        result.Diagnostics.Should().NotContain(d => d.Code == "SIG0326");
    }
}
