using System.IO;
using System.Text;
using FluentAssertions;
using SigilBuild.Wrapper.Codec;
using SigilBuild.Wrapper.Engine;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Engine;

/// <summary>
/// Unit coverage for <see cref="PayloadExtraction"/> — the temp-dir owner that
/// unpacks the embedded <c>SIGIL_PAYLOAD_V2</c> zstd container and removes it on
/// <see cref="PayloadExtraction.Dispose"/>.
/// </summary>
public sealed class PayloadExtractionTests
{
    [Fact]
    public void Extract_unpacks_entries_and_places_the_dir_under_temp()
    {
        var container = BuildPayload(
            ("app/app.exe", "APP"),
            ("app/data/readme.txt", "hello"));

        var extraction = PayloadExtraction.Extract(container, "com.acme.Studio");
        try
        {
            extraction.Root.Should().StartWith(Path.GetTempPath());
            Path.GetFileName(extraction.Root).Should().StartWith("sigil-");

            File.ReadAllText(Path.Combine(extraction.Root, "app", "app.exe")).Should().Be("APP");
            File.ReadAllText(Path.Combine(extraction.Root, "app", "data", "readme.txt")).Should().Be("hello");
        }
        finally
        {
            extraction.Dispose();
        }
    }

    [Fact]
    public void Dispose_removes_the_extracted_directory()
    {
        var container = BuildPayload(("app/app.exe", "APP"));
        var extraction = PayloadExtraction.Extract(container, "com.acme.Studio");
        var root = extraction.Root;
        Directory.Exists(root).Should().BeTrue();

        extraction.Dispose();

        Directory.Exists(root).Should().BeFalse();
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        var container = BuildPayload(("a.txt", "a"));
        var extraction = PayloadExtraction.Extract(container, "app");

        extraction.Dispose();
        // A second dispose must not throw even though the dir is already gone.
        extraction.Dispose();

        Directory.Exists(extraction.Root).Should().BeFalse();
    }

    [Fact]
    public void Extract_rejects_zip_slip_entries()
    {
        var container = BuildPayload(("../escape.txt", "evil"));

        var act = () => PayloadExtraction.Extract(container, "zipslip-probe");

        act.Should().Throw<InvalidDataException>();
        // A traversal attempt must not leave a temp directory behind.
        Directory.EnumerateDirectories(Path.GetTempPath(), "sigil-zipslip-probe-*")
            .Should().BeEmpty();
    }

    /// <summary>
    /// Build a deterministic <c>SIGIL_PAYLOAD_V2</c> zstd container (T6) via the
    /// shared <see cref="PayloadCodec"/> — the same encoder the packager uses — so
    /// the extraction tests exercise the real on-disk container format.
    /// </summary>
    internal static byte[] BuildPayload(params (string Path, string Content)[] entries)
    {
        var payloadEntries = new PayloadEntry[entries.Length];
        for (var i = 0; i < entries.Length; i++)
        {
            payloadEntries[i] = new PayloadEntry(
                entries[i].Path, Encoding.UTF8.GetBytes(entries[i].Content));
        }

        return PayloadCodec.Encode(payloadEntries);
    }
}
