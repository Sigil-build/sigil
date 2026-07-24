using System.Text.Json;
using FluentAssertions;
using SigilBuild.Core.Diagnostics;
using SigilBuild.Wrapper.Json;
using SigilBuild.Wrapper.Update;

namespace SigilBuild.Wrapper.Tests.Update;

/// <summary>
/// Tests for the P12 (T12.1) channel-manifest contract: the
/// <see cref="ChannelManifest"/> record, its source-generated
/// <see cref="ChannelManifestJsonContext"/>, and <see cref="ChannelManifestParser"/>'s
/// SIG0320 malformed-manifest handling. Signature verification (T12.2) and the
/// <c>/Update</c> runtime (T12.3) are out of scope here.
/// </summary>
public class ChannelManifestParserTests
{
    private const string ValidJson = """
        {
          "schemaVersion": 1,
          "version": "2.3.0",
          "packageUrl": "https://updates.example.com/acme/2.3.0/package.zip",
          "sha256": "b1946ac92492d2347c6235b4d2611184",
          "minFromVersion": "2.0.0"
        }
        """;

    // ── Valid parse ─────────────────────────────────────────────────────────

    [Fact]
    public void Valid_manifest_json_parses_to_the_expected_ChannelManifest()
    {
        var result = ChannelManifestParser.Parse(ValidJson);

        result.Success.Should().BeTrue();
        result.DiagnosticCode.Should().BeNull();
        result.Error.Should().BeNull();
        result.Manifest.Should().NotBeNull();
        result.Manifest!.SchemaVersion.Should().Be(1);
        result.Manifest.Version.Should().Be("2.3.0");
        result.Manifest.PackageUrl.Should().Be("https://updates.example.com/acme/2.3.0/package.zip");
        result.Manifest.Sha256.Should().Be("b1946ac92492d2347c6235b4d2611184");
        result.Manifest.MinFromVersion.Should().Be("2.0.0");
    }

    [Fact]
    public void MinFromVersion_is_optional_and_defaults_to_null()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "version": "1.0.0",
              "packageUrl": "https://updates.example.com/pkg.zip",
              "sha256": "abc123"
            }
            """;

        var result = ChannelManifestParser.Parse(json);

        result.Success.Should().BeTrue();
        result.Manifest!.MinFromVersion.Should().BeNull();
    }

    // ── Source-gen round-trip ───────────────────────────────────────────────

    [Fact]
    public void ChannelManifest_round_trips_through_the_source_generated_context()
    {
        var original = new ChannelManifest(
            SchemaVersion: 1,
            Version: "5.1.2",
            PackageUrl: "https://updates.example.com/app/5.1.2/full.zip",
            Sha256: "deadbeefcafebabe",
            MinFromVersion: "4.0.0");

        var json = JsonSerializer.Serialize(original, ChannelManifestJsonContext.Default.ChannelManifest);
        var back = JsonSerializer.Deserialize(json, ChannelManifestJsonContext.Default.ChannelManifest);

        back.Should().NotBeNull();
        back.Should().Be(original);
    }

    [Fact]
    public void Null_MinFromVersion_round_trips_and_is_omitted_from_the_wire_json()
    {
        var original = new ChannelManifest(1, "1.0.0", "https://x/pkg.zip", "abc");

        var json = JsonSerializer.Serialize(original, ChannelManifestJsonContext.Default.ChannelManifest);
        json.Should().NotContain("minFromVersion", "DefaultIgnoreCondition=WhenWritingNull omits null optional fields");

        var back = JsonSerializer.Deserialize(json, ChannelManifestJsonContext.Default.ChannelManifest);
        back.Should().Be(original);
    }

    // ── SIG0320: malformed JSON ──────────────────────────────────────────────

    [Fact]
    public void Malformed_json_fails_with_SIG0320()
    {
        var result = ChannelManifestParser.Parse("{ not valid json ");

        result.Success.Should().BeFalse();
        result.Manifest.Should().BeNull();
        result.DiagnosticCode.Should().Be(DiagnosticCodes.MalformedChannelManifest);
        result.DiagnosticCode.Should().Be("SIG0320");
        result.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Empty_string_fails_with_SIG0320()
    {
        var result = ChannelManifestParser.Parse(string.Empty);

        result.Success.Should().BeFalse();
        result.DiagnosticCode.Should().Be(DiagnosticCodes.MalformedChannelManifest);
    }

    // ── SIG0320: missing required fields ────────────────────────────────────

    [Fact]
    public void Missing_version_fails_with_SIG0320()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "packageUrl": "https://updates.example.com/pkg.zip",
              "sha256": "abc123"
            }
            """;

        var result = ChannelManifestParser.Parse(json);

        result.Success.Should().BeFalse();
        result.DiagnosticCode.Should().Be(DiagnosticCodes.MalformedChannelManifest);
        result.Error.Should().Contain("version");
    }

    [Fact]
    public void Missing_packageUrl_fails_with_SIG0320()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "version": "1.0.0",
              "sha256": "abc123"
            }
            """;

        var result = ChannelManifestParser.Parse(json);

        result.Success.Should().BeFalse();
        result.DiagnosticCode.Should().Be(DiagnosticCodes.MalformedChannelManifest);
        result.Error.Should().Contain("packageUrl");
    }

    [Fact]
    public void Missing_sha256_fails_with_SIG0320()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "version": "1.0.0",
              "packageUrl": "https://updates.example.com/pkg.zip"
            }
            """;

        var result = ChannelManifestParser.Parse(json);

        result.Success.Should().BeFalse();
        result.DiagnosticCode.Should().Be(DiagnosticCodes.MalformedChannelManifest);
        result.Error.Should().Contain("sha256");
    }

    // ── SIG0320: non-HTTPS packageUrl ────────────────────────────────────────

    [Theory]
    [InlineData("http://updates.example.com/pkg.zip")]
    [InlineData("ftp://updates.example.com/pkg.zip")]
    [InlineData("file:///C:/pkg.zip")]
    [InlineData("not-a-url-at-all")]
    public void Non_https_packageUrl_fails_with_SIG0320(string insecureUrl)
    {
        var json = $$"""
            {
              "schemaVersion": 1,
              "version": "1.0.0",
              "packageUrl": "{{insecureUrl}}",
              "sha256": "abc123"
            }
            """;

        var result = ChannelManifestParser.Parse(json);

        result.Success.Should().BeFalse();
        result.DiagnosticCode.Should().Be(DiagnosticCodes.MalformedChannelManifest);
        result.Error.Should().Contain("https");
    }

    // ── SIG0320: unsupported schemaVersion ───────────────────────────────────

    [Fact]
    public void Unsupported_schemaVersion_fails_with_SIG0320()
    {
        const string json = """
            {
              "schemaVersion": 2,
              "version": "1.0.0",
              "packageUrl": "https://updates.example.com/pkg.zip",
              "sha256": "abc123"
            }
            """;

        var result = ChannelManifestParser.Parse(json);

        result.Success.Should().BeFalse();
        result.DiagnosticCode.Should().Be(DiagnosticCodes.MalformedChannelManifest);
        result.Error.Should().Contain("schemaVersion");
    }

    [Fact]
    public void Missing_schemaVersion_defaults_to_zero_and_fails_with_SIG0320()
    {
        const string json = """
            {
              "version": "1.0.0",
              "packageUrl": "https://updates.example.com/pkg.zip",
              "sha256": "abc123"
            }
            """;

        var result = ChannelManifestParser.Parse(json);

        result.Success.Should().BeFalse();
        result.DiagnosticCode.Should().Be(DiagnosticCodes.MalformedChannelManifest);
    }
}
