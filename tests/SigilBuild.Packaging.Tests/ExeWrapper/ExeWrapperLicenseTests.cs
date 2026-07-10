using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using SigilBuild.Core.Diagnostics;
using SigilBuild.Core.Manifest;
using SigilBuild.Packaging.ExeWrapper;
using SigilBuild.Wrapper.Json;
using Xunit;

namespace SigilBuild.Packaging.Tests.ExeWrapper;

/// <summary>
/// T14 pack → blob coverage: <see cref="ExeWrapperPackager.BuildBlobBytes"/> reads
/// the manifest-referenced license file, embeds its text into the
/// <c>SIGIL_BLOB_V1</c> wire payload, and emits a non-fatal
/// <see cref="DiagnosticCodes.LicenseFileUnreadable"/> when the file is
/// missing/unreadable/empty (the pack still succeeds; the License screen is omitted).
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
            Installer: new InstallerSection(null, License: licensePath),
            Location: SourceLocation.Unknown);

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

            s.LicenseText.Should().Be(text);
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
}
