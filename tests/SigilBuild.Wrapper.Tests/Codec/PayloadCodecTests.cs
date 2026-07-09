using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using FluentAssertions;
using SigilBuild.Wrapper.Codec;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Codec;

/// <summary>
/// Unit coverage for the shared zstd <see cref="PayloadCodec"/> (T6): the
/// <c>SIGIL_PAYLOAD_V2</c> container round-trips, is deterministic (byte-identical
/// across encodes and independent of input order), and rejects malformed input.
/// </summary>
public sealed class PayloadCodecTests
{
    [Fact]
    public void Encode_then_decode_round_trips_every_entry()
    {
        var entries = new[]
        {
            new PayloadEntry("app/app.exe", Encoding.UTF8.GetBytes("APP-BYTES")),
            new PayloadEntry("app/data/readme.txt", Encoding.UTF8.GetBytes("hello world")),
            new PayloadEntry("empty.bin", Array.Empty<byte>()),
            new PayloadEntry("big.bin", RepeatingBytes(200_000)),
        };

        var container = PayloadCodec.Encode(entries);

        var decoded = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        PayloadCodec.Decode(container, (path, content) => decoded[path] = content);

        decoded.Should().HaveCount(entries.Length);
        foreach (var e in entries)
        {
            decoded.Should().ContainKey(e.RelativePath);
            decoded[e.RelativePath].Should().Equal(e.Content);
        }
    }

    [Fact]
    public void Encode_is_byte_identical_across_two_encodes_of_the_same_input()
    {
        var entries = new[]
        {
            new PayloadEntry("a.txt", Encoding.UTF8.GetBytes("alpha")),
            new PayloadEntry("dir/b.bin", RepeatingBytes(50_000)),
        };

        var first = PayloadCodec.Encode(entries);
        var second = PayloadCodec.Encode(entries);

        second.Should().Equal(first, "a fixed level + no stored timestamps make the container deterministic");
    }

    [Fact]
    public void Encode_is_independent_of_entry_order()
    {
        var content1 = Encoding.UTF8.GetBytes("one");
        var content2 = Encoding.UTF8.GetBytes("two");
        var content3 = RepeatingBytes(10_000);

        var forward = PayloadCodec.Encode(new[]
        {
            new PayloadEntry("a", content1),
            new PayloadEntry("b", content2),
            new PayloadEntry("c", content3),
        });
        var shuffled = PayloadCodec.Encode(new[]
        {
            new PayloadEntry("c", content3),
            new PayloadEntry("a", content1),
            new PayloadEntry("b", content2),
        });

        shuffled.Should().Equal(forward, "entries are sorted by ordinal relative path before framing");
    }

    [Fact]
    public void Encode_normalizes_backslash_separators_to_forward_slash()
    {
        var container = PayloadCodec.Encode(new[]
        {
            new PayloadEntry(@"app\sub\file.txt", Encoding.UTF8.GetBytes("x")),
        });

        var paths = new List<string>();
        PayloadCodec.Decode(container, (path, _) => paths.Add(path));

        paths.Should().ContainSingle().Which.Should().Be("app/sub/file.txt");
    }

    [Fact]
    public void Decode_rejects_bytes_that_are_not_a_v2_container()
    {
        var act = () => PayloadCodec.Decode(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, (_, _) => { });

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Decode_rejects_a_truncated_container()
    {
        var container = PayloadCodec.Encode(new[]
        {
            new PayloadEntry("a.txt", Encoding.UTF8.GetBytes("some content here")),
        });
        var truncated = container.AsSpan(0, container.Length - 4).ToArray();

        var act = () => PayloadCodec.Decode(truncated, (_, _) => { });

        act.Should().Throw<InvalidDataException>();
    }

    private static byte[] RepeatingBytes(int length)
    {
        var buffer = new byte[length];
        for (var i = 0; i < length; i++)
        {
            buffer[i] = (byte)(i % 251);
        }
        return buffer;
    }
}
