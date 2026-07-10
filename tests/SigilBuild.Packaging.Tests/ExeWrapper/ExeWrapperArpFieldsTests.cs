using System.IO;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Packaging.ExeWrapper;
using SigilBuild.Wrapper.Json;
using Xunit;

namespace SigilBuild.Packaging.Tests.ExeWrapper;

/// <summary>
/// T10 pack → blob coverage: <see cref="ExeWrapperPackager.BuildBlobBytes"/>
/// threads the real Add/Remove Programs fields into the blob — DisplayName from
/// <c>App.Name</c>, Publisher from <c>App.Publisher</c>, Version from
/// <c>App.Version</c>, and EstimatedSizeBytes from the uncompressed payload
/// footprint — replacing the former AppId / "1.0.0" / "Unknown" / 0 placeholders.
/// </summary>
public class ExeWrapperArpFieldsTests
{
    private static SerializableWrapperBlob Deserialize(byte[] blob) =>
        JsonSerializer.Deserialize(
            Encoding.UTF8.GetString(blob),
            WrapperBlobJsonContext.Default.SerializableWrapperBlob)!;

    private static SigilManifest Manifest() =>
        new("v1.0",
            new AppSection("com.acme.Studio", "Acme Studio", "3.2.0", "Acme, Inc.", null, null),
            new BuildSection("./out", null, null, true),
            null, null, null, null,
            Installer: null,
            Location: SourceLocation.Unknown);

    [Fact]
    public void BuildBlobBytes_threads_real_App_fields_into_the_blob()
    {
        var blob = Deserialize(ExeWrapperPackager.BuildBlobBytes(Manifest(), string.Empty));

        blob.DisplayName.Should().Be("Acme Studio", "DisplayName ← App.Name");
        blob.Publisher.Should().Be("Acme, Inc.", "Publisher ← App.Publisher");
        blob.Version.Should().Be("3.2.0", "Version ← App.Version");
    }

    [Fact]
    public void BuildBlobBytes_estimated_size_is_the_uncompressed_payload_footprint()
    {
        var src = Path.Combine(Path.GetTempPath(), "sigil-arp-size-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(src);
        try
        {
            // Two files whose byte lengths sum to a known, non-zero footprint.
            File.WriteAllBytes(Path.Combine(src, "a.bin"), new byte[1000]);
            Directory.CreateDirectory(Path.Combine(src, "sub"));
            File.WriteAllBytes(Path.Combine(src, "sub", "b.bin"), new byte[234]);

            var blob = Deserialize(ExeWrapperPackager.BuildBlobBytes(Manifest(), src));

            blob.EstimatedSizeBytes.Should().Be(1234,
                "EstimatedSize is the sum of the uncompressed payload file sizes (the on-disk footprint)");
        }
        finally
        {
            Directory.Delete(src, recursive: true);
        }
    }

    [Fact]
    public void ComputeInstalledSizeBytes_is_zero_for_a_missing_source_directory()
    {
        ExeWrapperPackager.ComputeInstalledSizeBytes(string.Empty).Should().Be(0);
        ExeWrapperPackager.ComputeInstalledSizeBytes(
            Path.Combine(Path.GetTempPath(), "sigil-does-not-exist-" + System.Guid.NewGuid().ToString("N")))
            .Should().Be(0);
    }
}
