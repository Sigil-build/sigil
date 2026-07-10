using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using FluentAssertions;
using SigilBuild.Packaging.ExeWrapper;
using SigilBuild.Wrapper.Engine;
using Xunit;

namespace SigilBuild.Packaging.Tests.ExeWrapper;

/// <summary>
/// T6 coverage for the packager's zstd payload container: two packs of the same
/// source directory produce byte-identical <c>SIGIL_PAYLOAD_V2</c> bytes, and the
/// container round-trips through the host-side <see cref="PayloadExtraction"/>
/// decoder so packed files land verbatim.
/// </summary>
public sealed class ExeWrapperPayloadTests
{
    [Fact]
    public void BuildPayloadBytes_is_byte_identical_across_two_packs_of_the_same_input()
    {
        var source = CreateSource(
            ("app/app.exe", "APP-BYTES"),
            ("app/data/readme.txt", "hello"),
            ("root.txt", "top"));
        try
        {
            var first = ExeWrapperPackager.BuildPayloadBytes(source, CancellationToken.None);
            var second = ExeWrapperPackager.BuildPayloadBytes(source, CancellationToken.None);

            first.Should().NotBeEmpty();
            second.Should().Equal(first,
                "the zstd container pins the level and stores no timestamps, so packing is deterministic");
        }
        finally
        {
            Directory.Delete(source, recursive: true);
        }
    }

    [Fact]
    public void BuildPayloadBytes_round_trips_through_the_host_extractor()
    {
        var files = new[]
        {
            ("app/app.exe", "APP-BYTES"),
            ("app/data/readme.txt", "hello world"),
            ("root.txt", "top-level"),
        };
        var source = CreateSource(files);
        try
        {
            var container = ExeWrapperPackager.BuildPayloadBytes(source, CancellationToken.None);

            var extraction = PayloadExtraction.Extract(container, "com.acme.Studio");
            try
            {
                foreach (var (rel, content) in files)
                {
                    var landed = Path.Combine(extraction.Root, rel.Replace('/', Path.DirectorySeparatorChar));
                    File.Exists(landed).Should().BeTrue($"'{rel}' must extract from the container");
                    File.ReadAllText(landed).Should().Be(content);
                }
            }
            finally
            {
                extraction.Dispose();
            }
        }
        finally
        {
            Directory.Delete(source, recursive: true);
        }
    }

    [Fact]
    public void BuildPayloadBytes_returns_empty_for_a_missing_source_directory()
    {
        var bytes = ExeWrapperPackager.BuildPayloadBytes(
            Path.Combine(Path.GetTempPath(), "sigil-no-such-" + Guid.NewGuid().ToString("N")),
            CancellationToken.None);

        bytes.Should().BeEmpty();
    }

    private static string CreateSource(params (string RelPath, string Content)[] files)
    {
        var root = Path.Combine(Path.GetTempPath(), "sigil-payload-src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        foreach (var (rel, content) in files)
        {
            var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        return root;
    }
}
