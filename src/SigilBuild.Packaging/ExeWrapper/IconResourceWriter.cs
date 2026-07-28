using System;
using System.Buffers.Binary;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SigilBuild.Packaging.ExeWrapper;

/// <summary>
/// Replaces the produced setup.exe's icon by writing RT_ICON entries and a
/// single RT_GROUP_ICON entry that references them. Drives the Explorer
/// icon shown for setup.exe in File Explorer + Start menu pins.
/// </summary>
/// <remarks>
/// The .ico format on disk is:
///   - ICONDIR (6 bytes):  reserved=0 (u16), type=1 (u16, icon), count (u16)
///   - ICONDIRENTRY[count] (16 bytes each): width, height, color-count,
///       reserved, planes, bit-count, size-in-bytes, byte-offset-into-file
///   - imageData[count]: raw PNG / BMP DIB bytes
///
/// The PE RT_GROUP_ICON format is identical EXCEPT the trailing u32
/// byte-offset is replaced with a u16 RT_ICON resource id. Each RT_ICON
/// resource holds the image data of one ICONDIRENTRY.
/// </remarks>
internal static partial class IconResourceWriter
{
    private static readonly IntPtr RtIcon = (IntPtr)3;
    private static readonly IntPtr RtGroupIcon = (IntPtr)14;

    private const ushort LangNeutral = 0;
    private const string GroupIconName = "MAINICON";

    public static Task WriteAsync(string exePath, byte[] icoBytes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(exePath);
        ArgumentNullException.ThrowIfNull(icoBytes);
        ct.ThrowIfCancellationRequested();
        WriteCore(exePath, icoBytes);
        return Task.CompletedTask;
    }

    private static void WriteCore(string exePath, byte[] icoBytes)
    {
        var (entries, images) = ParseIcoFile(icoBytes);
        var hUpdate = BeginUpdateResourceW(exePath, deleteExistingResources: false);
        if (hUpdate == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                $"BeginUpdateResource failed for '{exePath}'");

        var committed = false;
        try
        {
            for (var i = 0; i < images.Length; i++)
            {
                UpdateOne(hUpdate, RtIcon, MakeIntResource((ushort)(i + 1)), images[i]);
            }

            var groupBlob = BuildGroupIconBlob(entries);
            using var namePtr = NativeUtf16Buffer.Alloc(GroupIconName);
            UpdateOne(hUpdate, RtGroupIcon, namePtr.Pointer, groupBlob);

            if (!EndUpdateResourceW(hUpdate, fDiscard: false))
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    $"EndUpdateResource (commit) failed for '{exePath}'");
            committed = true;
        }
        finally
        {
            // EndUpdateResource is called at most once per hUpdate: either the
            // commit above ran (and freed the handle) or we discard here on
            // any path that did not reach `committed = true`.
            if (!committed)
            {
                _ = EndUpdateResourceW(hUpdate, fDiscard: true);
            }
        }
    }

    private readonly record struct IcoEntry(
        byte Width, byte Height, byte ColorCount, byte Reserved,
        ushort Planes, ushort BitCount, uint SizeBytes, uint OffsetBytes);

    private static (IcoEntry[] Entries, byte[][] Images) ParseIcoFile(byte[] ico)
    {
        if (ico.Length < 6)
            throw new InvalidDataException("ico file too short for ICONDIR header");

        var reserved = BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(0));
        var type = BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(2));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(4));
        if (reserved != 0 || type != 1 || count == 0)
            throw new InvalidDataException(
                $"invalid ICONDIR: reserved={reserved}, type={type}, count={count}");

        var entries = new IcoEntry[count];
        var images = new byte[count][];
        var pos = 6;
        for (var i = 0; i < count; i++)
        {
            if (pos + 16 > ico.Length)
                throw new InvalidDataException($"ICONDIRENTRY {i} truncated");
            var e = new IcoEntry(
                Width: ico[pos + 0],
                Height: ico[pos + 1],
                ColorCount: ico[pos + 2],
                Reserved: ico[pos + 3],
                Planes: BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(pos + 4)),
                BitCount: BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(pos + 6)),
                SizeBytes: BinaryPrimitives.ReadUInt32LittleEndian(ico.AsSpan(pos + 8)),
                OffsetBytes: BinaryPrimitives.ReadUInt32LittleEndian(ico.AsSpan(pos + 12)));
            entries[i] = e;
            if (e.OffsetBytes + e.SizeBytes > ico.Length)
                throw new InvalidDataException($"ICONDIRENTRY {i} image data overruns file");
            images[i] = new byte[e.SizeBytes];
            Array.Copy(ico, (int)e.OffsetBytes, images[i], 0, (int)e.SizeBytes);
            pos += 16;
        }
        return (entries, images);
    }

    private static byte[] BuildGroupIconBlob(IcoEntry[] entries)
    {
        var blob = new byte[6 + entries.Length * 14];
        BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(0), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(4), (ushort)entries.Length);

        var pos = 6;
        for (var i = 0; i < entries.Length; i++)
        {
            blob[pos + 0] = entries[i].Width;
            blob[pos + 1] = entries[i].Height;
            blob[pos + 2] = entries[i].ColorCount;
            blob[pos + 3] = entries[i].Reserved;
            BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(pos + 4), entries[i].Planes);
            BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(pos + 6), entries[i].BitCount);
            BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(pos + 8), entries[i].SizeBytes);
            BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(pos + 12), (ushort)(i + 1));
            pos += 14;
        }
        return blob;
    }

    private static void UpdateOne(IntPtr hUpdate, IntPtr type, IntPtr namePtr, byte[] data)
    {
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            if (!UpdateResourceW(hUpdate, type, namePtr, LangNeutral,
                                 handle.AddrOfPinnedObject(), (uint)data.Length))
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "UpdateResource failed for icon resource");
        }
        finally { handle.Free(); }
    }

    private static IntPtr MakeIntResource(ushort id) => (IntPtr)id;

    private readonly struct NativeUtf16Buffer : IDisposable
    {
        public IntPtr Pointer { get; }
        private NativeUtf16Buffer(IntPtr p) { Pointer = p; }
        public static NativeUtf16Buffer Alloc(string s) =>
            new(Marshal.StringToHGlobalUni(s));
        public void Dispose() { if (Pointer != IntPtr.Zero) Marshal.FreeHGlobal(Pointer); }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "BeginUpdateResourceW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.SysInt)]
    private static partial IntPtr BeginUpdateResourceW(string pFileName,
        [MarshalAs(UnmanagedType.Bool)] bool deleteExistingResources);

    [LibraryImport("kernel32.dll", EntryPoint = "UpdateResourceW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateResourceW(IntPtr hUpdate, IntPtr lpType,
        IntPtr lpName, ushort wLanguage, IntPtr lpData, uint cbData);

    [LibraryImport("kernel32.dll", EntryPoint = "EndUpdateResourceW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EndUpdateResourceW(IntPtr hUpdate,
        [MarshalAs(UnmanagedType.Bool)] bool fDiscard);
}
