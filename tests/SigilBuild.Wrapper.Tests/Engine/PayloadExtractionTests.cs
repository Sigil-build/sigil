using System.IO;
using System.IO.Compression;
using System.Text;
using FluentAssertions;
using SigilBuild.Wrapper.Engine;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Engine;

/// <summary>
/// Unit coverage for <see cref="PayloadExtraction"/> — the temp-dir owner that
/// unpacks the embedded <c>SIGIL_PAYLOAD_V1</c> archive and removes it on
/// <see cref="PayloadExtraction.Dispose"/>.
/// </summary>
public sealed class PayloadExtractionTests
{
    [Fact]
    public void Extract_unpacks_entries_and_places_the_dir_under_temp()
    {
        var zip = BuildZip(
            ("app/app.exe", "APP"),
            ("app/data/readme.txt", "hello"));

        var extraction = PayloadExtraction.Extract(zip, "com.acme.Studio");
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
        var zip = BuildZip(("app/app.exe", "APP"));
        var extraction = PayloadExtraction.Extract(zip, "com.acme.Studio");
        var root = extraction.Root;
        Directory.Exists(root).Should().BeTrue();

        extraction.Dispose();

        Directory.Exists(root).Should().BeFalse();
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        var zip = BuildZip(("a.txt", "a"));
        var extraction = PayloadExtraction.Extract(zip, "app");

        extraction.Dispose();
        // A second dispose must not throw even though the dir is already gone.
        extraction.Dispose();

        Directory.Exists(extraction.Root).Should().BeFalse();
    }

    [Fact]
    public void Extract_rejects_zip_slip_entries()
    {
        var zip = BuildZip(("../escape.txt", "evil"));

        var act = () => PayloadExtraction.Extract(zip, "zipslip-probe");

        act.Should().Throw<InvalidDataException>();
        // A traversal attempt must not leave a temp directory behind.
        Directory.EnumerateDirectories(Path.GetTempPath(), "sigil-zipslip-probe-*")
            .Should().BeEmpty();
    }

    internal static byte[] BuildZip(params (string Path, string Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
                using var s = entry.Open();
                var bytes = Encoding.UTF8.GetBytes(content);
                s.Write(bytes, 0, bytes.Length);
            }
        }
        return ms.ToArray();
    }
}
