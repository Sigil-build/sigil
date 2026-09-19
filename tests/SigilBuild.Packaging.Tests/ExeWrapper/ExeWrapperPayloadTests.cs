// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

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
/// Coverage for the packager's zstd payload container: two packs of the same
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

    /// <summary>
    /// An EMPTY payload is not an empty byte array — the container header is
    /// written for zero entries too. That is exactly the distinction that let a
    /// pack of an empty source directory produce an installer which reported
    /// success, registered in Add/Remove Programs, and laid down nothing.
    /// </summary>
    [Fact]
    public void An_empty_source_directory_still_produces_container_bytes_but_no_entries()
    {
        var source = CreateSource();
        try
        {
            var container = ExeWrapperPackager.BuildPayloadBytes(source, CancellationToken.None);

            container.Should().NotBeEmpty("the header is written regardless");
            SigilBuild.Wrapper.Codec.PayloadCodec.EntryCount(container).Should().Be(0,
                "a length check cannot tell an empty payload from a full one — only the count can");
        }
        finally
        {
            Directory.Delete(source, recursive: true);
        }
    }

    [Fact]
    public void EntryCount_reads_zero_for_anything_that_is_not_a_payload()
    {
        // Absent, truncated, and foreign all mean the same thing to every caller:
        // no payload is available here.
        SigilBuild.Wrapper.Codec.PayloadCodec.EntryCount(Array.Empty<byte>()).Should().Be(0);
        SigilBuild.Wrapper.Codec.PayloadCodec.EntryCount(new byte[] { 0x53, 0x47 }).Should().Be(0);
        SigilBuild.Wrapper.Codec.PayloadCodec.EntryCount(
            Encoding.UTF8.GetBytes("not a container at all")).Should().Be(0);
    }

    [Fact]
    public void EntryCount_matches_what_was_packed()
    {
        var source = CreateSource(("a.txt", "1"), ("b/c.txt", "2"), ("b/d/e.txt", "3"));
        try
        {
            var container = ExeWrapperPackager.BuildPayloadBytes(source, CancellationToken.None);
            SigilBuild.Wrapper.Codec.PayloadCodec.EntryCount(container).Should().Be(3);
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
