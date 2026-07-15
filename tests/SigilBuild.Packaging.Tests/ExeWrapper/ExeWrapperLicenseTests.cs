using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using SigilBuild.Core.Configuration;
using SigilBuild.Core.Diagnostics;
using SigilBuild.Core.Manifest;
using SigilBuild.Packaging.ExeWrapper;
using SigilBuild.Wrapper.Json;
using Xunit;

namespace SigilBuild.Packaging.Tests.ExeWrapper;

/// <summary>
/// T14 / P9 (gap G10) pack → blob coverage: <see cref="ExeWrapperPackager.BuildBlobBytes"/>
/// reads each manifest-referenced license file into a tag -&gt; text map, embeds
/// it into the <c>SIGIL_BLOB_V1</c> wire payload, and splits diagnostic ownership
/// by failure kind (design §5.3): a per-entry read failure is a non-fatal
/// <see cref="DiagnosticCodes.LicenseFileUnreadable"/> (SIG0250); the resulting
/// map lacking an <c>en</c> entry is a fatal <see cref="DiagnosticCodes.LocalizedTextMissingEnglish"/>
/// (SIG0290); an entirely empty map omits the screen with neither diagnostic
/// (T14's original behavior, unchanged).
/// </summary>
public class ExeWrapperLicenseTests
{
    private static SerializableWrapperBlob Deserialize(byte[] blob) =>
        JsonSerializer.Deserialize(
            Encoding.UTF8.GetString(blob),
            WrapperBlobJsonContext.Default.SerializableWrapperBlob)!;

    private static SigilManifest ManifestWithLicense(string? licensePath) =>
        new("v1.0",
            new AppSection("com.example.App", "Example", "1.0.0", "Example Inc.", null, null),
            new BuildSection("./out", null, null, true),
            null, null, null, null,
            Installer: new InstallerSection(null, License: licensePath is null ? null : LocalizedText.Plain(licensePath)),
            Location: SourceLocation.Unknown);

    private const string ManifestPrelude = """
        spec: v1.0
        app:
          id: com.example.App
          name: Example
          version: 1.0.0
          publisher: Example Inc.
        build:
          source: ./out
        """;

    /// <summary>Creates a temp directory and writes each named fixture file into it.</summary>
    private static string CreateFixture(params (string Name, string Content)[] files)
    {
        var dir = Path.Combine(Path.GetTempPath(), "sigil-license-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        foreach (var (name, content) in files)
        {
            File.WriteAllText(Path.Combine(dir, name), content);
        }
        return dir;
    }

    /// <summary>Parses a minimal manifest (prelude + the given installer YAML) and packs it,
    /// returning the deserialized blob and the combined parse + pack diagnostics.</summary>
    private static SerializableWrapperBlob Pack(string sourceDirectory, string installerYaml, out List<Diagnostic> diagnostics)
    {
        var yaml = ManifestPrelude + "\n" + installerYaml;
        var parseResult = ManifestParser.Parse(yaml, "<inline>");
        parseResult.Manifest.Should().NotBeNull();

        diagnostics = new List<Diagnostic>(parseResult.Diagnostics);
        var blob = ExeWrapperPackager.BuildBlobBytes(parseResult.Manifest!, sourceDirectory, diagnostics);
        return Deserialize(blob);
    }

    [Fact]
    public void BuildBlobBytes_EmbedsLicenseText_WhenFilePresent()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sigil-license-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            const string text = "ACME EULA\n\nYou may use this software.";
            File.WriteAllText(Path.Combine(dir, "LICENSE.txt"), text);

            var diagnostics = new List<Diagnostic>();
            var blob = ExeWrapperPackager.BuildBlobBytes(ManifestWithLicense("./LICENSE.txt"), dir, diagnostics);
            var s = Deserialize(blob);

            s.LicenseText!["en"].Should().Be(text);
            diagnostics.Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void BuildBlobBytes_NoLicenseField_LeavesLicenseTextNull()
    {
        var blob = ExeWrapperPackager.BuildBlobBytes(ManifestWithLicense(null), string.Empty);
        Deserialize(blob).LicenseText.Should().BeNull();
    }

    [Fact]
    public void BuildBlobBytes_MissingFile_EmitsWarningAndOmitsLicense()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sigil-license-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var diagnostics = new List<Diagnostic>();
            var blob = ExeWrapperPackager.BuildBlobBytes(ManifestWithLicense("./NOPE.txt"), dir, diagnostics);
            var s = Deserialize(blob);

            s.LicenseText.Should().BeNull();
            diagnostics.Should().Contain(d =>
                d.Code == DiagnosticCodes.LicenseFileUnreadable &&
                d.Severity == DiagnosticSeverity.Warning &&
                d.Message.Contains("NOPE.txt"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void BuildBlobBytes_EmptyFile_EmitsWarningAndOmitsLicense()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sigil-license-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "EMPTY.txt"), "   \n");

            var diagnostics = new List<Diagnostic>();
            var blob = ExeWrapperPackager.BuildBlobBytes(ManifestWithLicense("./EMPTY.txt"), dir, diagnostics);
            var s = Deserialize(blob);

            s.LicenseText.Should().BeNull();
            diagnostics.Should().Contain(d =>
                d.Code == DiagnosticCodes.LicenseFileUnreadable &&
                d.Message.Contains("empty"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ── License map at pack time (P9, gap G10): SIG0250 -> SIG0290 ownership ──

    [Fact]
    public void License_PlainPath_ReadsAsEnglish()
    {
        var dir = CreateFixture(("LICENSE.txt", "Example EULA."));
        try
        {
            var blob = Pack(dir, "installer:\n  license: LICENSE.txt\n", out var diagnostics);

            blob.LicenseText!["en"].Should().Be("Example EULA.");
            diagnostics.Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void License_Map_ReadsEachFile()
    {
        var dir = CreateFixture(("LICENSE.txt", "Example EULA."), ("LICENSE.uk.txt", "Приклад ліцензії."));
        try
        {
            var blob = Pack(dir, "installer:\n  license:\n    en: LICENSE.txt\n    uk: LICENSE.uk.txt\n", out var diagnostics);

            blob.LicenseText!["en"].Should().Be("Example EULA.");
            blob.LicenseText!["uk"].Should().Be("Приклад ліцензії.");
            diagnostics.Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // The composite case §5.3 exists to catch: without ordering, this packs a
    // uk-only license and renders blank for everyone else.
    [Fact]
    public void License_UnreadableEnglish_Drops250_ThenFails290()
    {
        var dir = CreateFixture(("LICENSE.uk.txt", "Приклад ліцензії."));
        try
        {
            Pack(dir, "installer:\n  license:\n    en: missing.txt\n    uk: LICENSE.uk.txt\n", out var diagnostics);

            diagnostics.Should().Contain(d => d.Code == DiagnosticCodes.LicenseFileUnreadable);
            diagnostics.Should().Contain(d =>
                d.Code == DiagnosticCodes.LocalizedTextMissingEnglish && d.Severity == DiagnosticSeverity.Error);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // T14's behavior must survive: nothing readable => screen omitted, no SIG0290.
    [Fact]
    public void License_AllEntriesUnreadable_OmitsScreen_WithoutSig0290()
    {
        var dir = CreateFixture();
        try
        {
            var blob = Pack(dir, "installer:\n  license:\n    en: missing.txt\n    uk: also-missing.txt\n", out var diagnostics);

            blob.LicenseText.Should().BeNull();
            diagnostics.Should().Contain(d => d.Code == DiagnosticCodes.LicenseFileUnreadable);
            diagnostics.Should().NotContain(d => d.Code == DiagnosticCodes.LocalizedTextMissingEnglish);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
