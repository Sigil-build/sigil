using System.Linq;
using FluentAssertions;
using SigilBuild.Core.Configuration;
using SigilBuild.Core.Diagnostics;
using SigilBuild.Core.Manifest;
using Xunit;

namespace SigilBuild.Core.Tests.Manifest;

/// <summary>
/// P4: pack-time validation of the <c>http_download</c> step — sha256 is required
/// and the URL must be https:// (both fatal, so the packer refuses to emit an
/// unchecked or plaintext download).
/// </summary>
public class HttpDownloadParseTests
{
    private static string Yaml(string stepBody) =>
        "spec: v1.0\n" +
        "app: { id: com.example.App, name: App, version: 0.1.0, publisher: P }\n" +
        "build: { source: ./out }\n" +
        "install_steps:\n" +
        "  - " + stepBody + "\n";

    [Fact]
    public void Valid_https_download_with_sha256_parses()
    {
        var result = ManifestParser.Parse(Yaml(
            "{ id: dl, type: http_download, url: \"https://ex.com/a.zip\", dest: \"{install_dir}/a.zip\", sha256: \"abc123\", timeout_seconds: 60, retries: 2 }"),
            "s.yaml");

        result.Diagnostics.Should().NotContain(d => d.Severity == DiagnosticSeverity.Error);
        var step = result.Manifest!.InstallSteps!.OfType<InstallStep.HttpDownload>().Single();
        step.Url.Should().Be("https://ex.com/a.zip");
        step.Dest.Should().Be("{install_dir}/a.zip");
        step.Sha256.Should().Be("abc123");
        step.TimeoutSeconds.Should().Be(60);
        step.Retries.Should().Be(2);
    }

    [Fact]
    public void Missing_sha256_is_a_fatal_SIG0236()
    {
        var result = ManifestParser.Parse(Yaml(
            "{ id: dl, type: http_download, url: \"https://ex.com/a.zip\", dest: \"a.zip\" }"),
            "s.yaml");

        result.Diagnostics.Should().Contain(d =>
            d.Code == DiagnosticCodes.HttpDownloadChecksumRequired && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Http_url_is_a_fatal_SIG0235()
    {
        var result = ManifestParser.Parse(Yaml(
            "{ id: dl, type: http_download, url: \"http://ex.com/a.zip\", dest: \"a.zip\", sha256: \"abc\" }"),
            "s.yaml");

        result.Diagnostics.Should().Contain(d =>
            d.Code == DiagnosticCodes.HttpDownloadInsecureUrl && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Missing_url_or_dest_is_a_missing_field_error()
    {
        ManifestParser.Parse(Yaml("{ id: dl, type: http_download, dest: \"a.zip\", sha256: \"abc\" }"), "s.yaml")
            .Diagnostics.Should().Contain(d => d.Code == DiagnosticCodes.MissingRequiredStepField);

        ManifestParser.Parse(Yaml("{ id: dl, type: http_download, url: \"https://ex.com/a\", sha256: \"abc\" }"), "s.yaml")
            .Diagnostics.Should().Contain(d => d.Code == DiagnosticCodes.MissingRequiredStepField);
    }
}
