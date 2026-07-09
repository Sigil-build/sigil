using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ZstdSharp;

namespace SigilBuild.Wrapper.Codec;

/// <summary>
/// The single, reusable zstd payload codec shared by the packager (encode) and
/// the Native-AOT installer host (decode). Homed in <c>Wrapper.Core</c> so both
/// sides call the same implementation, and so the future delta-update engine can
/// reuse it (spec section 5). Uses <c>ZstdSharp.Port</c> — a pure-managed C# port
/// of zstd, so there is no native <c>libzstd</c> to bundle and the decode side
/// publishes clean under Native AOT.
/// </summary>
/// <remarks>
/// <para><b>Container framing (<c>SIGIL_PAYLOAD_V2</c>).</b> A small, explicit,
/// self-describing binary layout — every multi-byte integer is little-endian:</para>
/// <list type="table">
///   <item><description><c>[0..4)</c>  magic <c>"SGP2"</c> (0x53 0x47 0x50 0x32)</description></item>
///   <item><description><c>[4]</c>     format version = 2</description></item>
///   <item><description><c>[5]</c>     flags = 0 (reserved)</description></item>
///   <item><description><c>[6..10)</c> entry count (uint32)</description></item>
/// </list>
/// <para>followed by <c>entry count</c> records, emitted in ascending
/// <see cref="StringComparer.Ordinal"/> order of the (forward-slash) relative
/// path:</para>
/// <list type="table">
///   <item><description>path byte length (uint32)</description></item>
///   <item><description>path bytes (UTF-8, <c>/</c> separators)</description></item>
///   <item><description>uncompressed length (uint64)</description></item>
///   <item><description>compressed length (uint64)</description></item>
///   <item><description>zstd frame bytes (length == compressed length)</description></item>
/// </list>
/// <para>Each file is an independent zstd frame carrying its own content-size
/// header, so decode needs no side-channel size table beyond the redundant
/// uncompressed-length field (kept for validation).</para>
/// <para><b>Determinism.</b> Output is byte-identical across builds because: the
/// container stores <em>no</em> timestamps or filesystem metadata; entries are
/// sorted by ordinal relative path independent of enumeration order; and every
/// frame is produced by a single-threaded, dictionary-free
/// <see cref="Compressor"/> at the fixed <see cref="CompressionLevel"/> — zstd is
/// deterministic for a given (input, level, library version), and the library
/// version is pinned centrally.</para>
/// </remarks>
internal static class PayloadCodec
{
    /// <summary>The container format version — mirrors the <c>SIGIL_PAYLOAD_V2</c> resource marker.</summary>
    public const byte FormatVersion = 2;

    /// <summary>
    /// Fixed zstd compression level. Pinned (not "optimal"/adaptive) so output is
    /// reproducible; level 19 is the top of the standard range, chosen because
    /// packing is an offline, one-shot cost while the resulting frame ships in
    /// every installer download.
    /// </summary>
    public const int CompressionLevel = 19;

    // "SGP2" — distinguishes the container from a bare zstd frame (0x28 B5 2F FD).
    private static ReadOnlySpan<byte> Magic => "SGP2"u8;

    private const int HeaderLength = 10; // magic(4) + version(1) + flags(1) + count(4)

    /// <summary>
    /// Encode a set of payload entries into a deterministic
    /// <c>SIGIL_PAYLOAD_V2</c> container. Entries are sorted by ordinal relative
    /// path internally, so the caller's enumeration order does not affect the
    /// bytes.
    /// </summary>
    public static byte[] Encode(IEnumerable<PayloadEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var ordered = entries
            .OrderBy(e => e.RelativePath, StringComparer.Ordinal)
            .ToArray();

        using var ms = new MemoryStream();

        Span<byte> u32 = stackalloc byte[4];
        Span<byte> u64 = stackalloc byte[8];

        ms.Write(Magic);
        ms.WriteByte(FormatVersion);
        ms.WriteByte(0); // flags
        BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)ordered.Length);
        ms.Write(u32);

        using var compressor = new Compressor(CompressionLevel);
        foreach (var entry in ordered)
        {
            var content = entry.Content ?? Array.Empty<byte>();
            var pathBytes = Encoding.UTF8.GetBytes(NormalizePath(entry.RelativePath));

            // Wrap returns a span into the compressor's internal buffer; copy it
            // out before the next Wrap call reuses that buffer.
            var compressed = compressor.Wrap(content).ToArray();

            BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)pathBytes.Length);
            ms.Write(u32);
            ms.Write(pathBytes);

            BinaryPrimitives.WriteUInt64LittleEndian(u64, (ulong)content.Length);
            ms.Write(u64);
            BinaryPrimitives.WriteUInt64LittleEndian(u64, (ulong)compressed.Length);
            ms.Write(u64);
            ms.Write(compressed);
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Decode a <c>SIGIL_PAYLOAD_V2</c> container, invoking <paramref name="onEntry"/>
    /// once per entry with its normalized (forward-slash) relative path and
    /// decompressed bytes, in stored order. The callback owns writing the bytes to
    /// disk and enforcing any path/zip-slip policy; this method only parses and
    /// decompresses.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// The bytes are not a well-formed <c>SIGIL_PAYLOAD_V2</c> container (bad magic,
    /// unsupported version, truncated framing, or a frame whose decompressed length
    /// disagrees with its recorded length).
    /// </exception>
    public static void Decode(byte[] container, Action<string, byte[]> onEntry)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(onEntry);

        if (container.Length < HeaderLength ||
            !container.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new InvalidDataException(
                "payload container is not a SIGIL_PAYLOAD_V2 archive (bad magic)");
        }

        var version = container[4];
        if (version != FormatVersion)
        {
            throw new InvalidDataException(
                $"unsupported payload container version {version}; expected {FormatVersion}");
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(container.AsSpan(6, 4));

        var offset = HeaderLength;
        using var decompressor = new Decompressor();
        for (uint i = 0; i < count; i++)
        {
            var pathLen = checked((int)ReadUInt32(container, ref offset));
            var path = Encoding.UTF8.GetString(Slice(container, ref offset, pathLen));

            var uncompressedLen = checked((int)ReadUInt64(container, ref offset));
            var compressedLen = checked((int)ReadUInt64(container, ref offset));
            var frame = Slice(container, ref offset, compressedLen);

            var decompressed = decompressor.Unwrap(frame, uncompressedLen).ToArray();
            if (decompressed.Length != uncompressedLen)
            {
                throw new InvalidDataException(
                    $"payload entry '{path}' decompressed to {decompressed.Length} bytes, expected {uncompressedLen}");
            }

            onEntry(path, decompressed);
        }
    }

    private static string NormalizePath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(relativePath);
        return relativePath.Replace('\\', '/');
    }

    private static uint ReadUInt32(byte[] buffer, ref int offset)
        => BinaryPrimitives.ReadUInt32LittleEndian(Slice(buffer, ref offset, 4));

    private static ulong ReadUInt64(byte[] buffer, ref int offset)
        => BinaryPrimitives.ReadUInt64LittleEndian(Slice(buffer, ref offset, 8));

    private static ReadOnlySpan<byte> Slice(byte[] buffer, ref int offset, int length)
    {
        if (length < 0 || offset + length > buffer.Length)
        {
            throw new InvalidDataException("payload container is truncated");
        }

        var span = buffer.AsSpan(offset, length);
        offset += length;
        return span;
    }
}
