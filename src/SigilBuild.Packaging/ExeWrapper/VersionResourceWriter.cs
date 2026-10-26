// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SigilBuild.Core.Manifest;

namespace SigilBuild.Packaging.ExeWrapper;

/// <summary>
/// The values a packed installer's <c>RT_VERSION</c> resource carries — what
/// Explorer shows under Properties → Details. Every string comes from the
/// publisher's manifest or the produced file name; none of it names Sigil. (R105)
/// </summary>
internal sealed record VersionResourceFields(
    string FileDescription,
    string ProductName,
    string CompanyName,
    string FileVersion,
    string ProductVersion,
    string OriginalFilename,
    string InternalName,
    string LegalCopyright,
    ushort Major,
    ushort Minor,
    ushort Patch,
    bool IsPrerelease)
{
    /// <summary>
    /// Maps the manifest's <c>app</c> section and the produced file name onto the
    /// version resource: FileDescription is <c>&lt;app.name&gt; Setup</c>,
    /// ProductName is <c>app.name</c>, CompanyName is <c>app.publisher</c>, both
    /// version strings are the full <c>app.version</c>, OriginalFilename and
    /// InternalName are the produced file name, and LegalCopyright is
    /// <c>app.copyright</c> or empty when the manifest declares none. (R105)
    /// </summary>
    public static VersionResourceFields FromManifest(AppSection app, string outputFileName)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(outputFileName);

        var (major, minor, patch, isPrerelease) = ParseVersionQuad(app.Version);
        return new VersionResourceFields(
            FileDescription: $"{app.Name} Setup",
            ProductName: app.Name,
            CompanyName: app.Publisher,
            FileVersion: app.Version,
            ProductVersion: app.Version,
            OriginalFilename: outputFileName,
            InternalName: outputFileName,
            LegalCopyright: app.Copyright ?? string.Empty,
            Major: major,
            Minor: minor,
            Patch: patch,
            IsPrerelease: isPrerelease);
    }

    /// <summary>
    /// Derives the numeric <c>VS_FIXEDFILEINFO</c> quad from a SemVer string: the
    /// core <c>major.minor.patch</c> becomes <c>major.minor.patch.0</c>. Each part
    /// is clamped to 0–65535 (a part above 65535 becomes 65535; a missing or
    /// non-numeric part becomes 0). Prerelease and build metadata never enter the
    /// quad — they stay in the version strings — and a prerelease sets
    /// <c>VS_FF_PRERELEASE</c>. (R105)
    /// </summary>
    internal static (ushort Major, ushort Minor, ushort Patch, bool IsPrerelease) ParseVersionQuad(
        string? version)
    {
        var text = version ?? string.Empty;
        var plus = text.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
            text = text[..plus];
        var dash = text.IndexOf('-', StringComparison.Ordinal);
        var isPrerelease = dash >= 0;
        if (isPrerelease)
            text = text[..dash];

        var parts = text.Split('.');
        return (ClampPart(parts, 0), ClampPart(parts, 1), ClampPart(parts, 2), isPrerelease);
    }

    private static ushort ClampPart(string[] parts, int index)
    {
        if (index >= parts.Length)
            return 0;
        var part = parts[index];
        if (part.Length == 0)
            return 0;
        foreach (var c in part)
        {
            if (c is < '0' or > '9')
                return 0;
        }
        // All digits: anything that does not fit (or fits but exceeds a WORD) clamps high.
        return ulong.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n <= ushort.MaxValue
            ? (ushort)n
            : ushort.MaxValue;
    }
}

/// <summary>
/// Replaces the produced installer's <c>RT_VERSION</c> resource with one built
/// from the publisher's manifest, so Properties → Details describes the
/// publisher's installer instead of the stock host it was stamped from. (R105)
/// </summary>
/// <remarks>
/// <para>
/// The resource is written as ID 1 (<c>VS_VERSION_INFO</c>) in the
/// language-neutral language (0), with one <c>StringTable</c> keyed
/// <c>000004B0</c> (language neutral, Unicode code page 1200) and a matching
/// <c>Translation</c> var of <c>0x0000, 0x04B0</c> — the same shape the
/// compiler gives the host itself. Every other language's ID-1 version entry
/// already in the file is deleted in the same update cycle, so the file ends
/// with exactly one version resource. (R105)
/// </para>
/// <para>
/// Layout (every structure starts on a 32-bit boundary; <c>wLength</c> covers
/// the structure and its children, excluding padding that follows it):
/// <code>
/// VS_VERSIONINFO   { wLength, wValueLength=52, wType=0, "VS_VERSION_INFO", pad, VS_FIXEDFILEINFO, pad, children }
///   StringFileInfo { wLength, 0, wType=1, "StringFileInfo", pad, children }
///     StringTable  { wLength, 0, wType=1, "000004B0", pad, children }
///       String     { wLength, wValueLength=chars incl. NUL, wType=1, key, pad, value + NUL }
///   VarFileInfo    { wLength, 0, wType=1, "VarFileInfo", pad, children }
///     Var          { wLength, wValueLength=4, wType=0, "Translation", pad, DWORD }
/// </code>
/// </para>
/// </remarks>
internal static unsafe partial class VersionResourceWriter
{
    private static readonly IntPtr RtVersion = (IntPtr)16;
    private static readonly IntPtr VersionInfoId = (IntPtr)1;

    internal const ushort LanguageNeutral = 0x0000;
    private const ushort CodePageUnicode = 0x04B0;
    internal const string StringTableKey = "000004B0";

    /// <summary>
    /// Upper bound on each string value, in UTF-16 code units. Every length in the
    /// structure is a 16-bit WORD, so an unbounded publisher-supplied value could
    /// overflow it; a longer value is cut at this length (never inside a
    /// surrogate pair). (R105)
    /// </summary>
    internal const int MaxValueLength = 1024;

    private const uint FixedFileInfoSignature = 0xFEEF04BD;
    private const uint FixedFileInfoStrucVersion = 0x00010000;
    private const uint FileFlagsMask = 0x0000003F;   // VS_FFI_FILEFLAGSMASK
    private const uint FileFlagPrerelease = 0x00000002; // VS_FF_PRERELEASE
    private const uint FileOsNtWindows32 = 0x00040004;  // VOS_NT_WINDOWS32
    private const uint FileTypeApp = 0x00000001;        // VFT_APP
    private const int FixedFileInfoSize = 52;

    private const uint LoadLibraryAsDataFile = 0x00000002;
    private const int ErrorResourceDataNotFound = 1812;
    private const int ErrorResourceTypeNotFound = 1813;
    private const int ErrorResourceNameNotFound = 1814;
    private const int ErrorResourceLangNotFound = 1815;

    public static Task WriteAsync(string exePath, VersionResourceFields fields, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(exePath);
        ArgumentNullException.ThrowIfNull(fields);
        ct.ThrowIfCancellationRequested();
        WriteCore(exePath, fields);
        return Task.CompletedTask;
    }

    private static void WriteCore(string exePath, VersionResourceFields fields)
    {
        var blob = Build(fields);

        // Enumerate before BeginUpdateResource: the datafile mapping is released
        // again before the update cycle opens the file for writing.
        var existingLanguages = ReadVersionLanguages(exePath);

        var hUpdate = BeginUpdateResourceW(exePath, deleteExistingResources: false);
        if (hUpdate == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                $"BeginUpdateResource failed for '{exePath}'");

        var committed = false;
        try
        {
            foreach (var language in existingLanguages)
            {
                if (language == LanguageNeutral)
                    continue; // overwritten in place below
                if (!UpdateResourceW(hUpdate, RtVersion, VersionInfoId, language, IntPtr.Zero, 0))
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        $"UpdateResource (delete RT_VERSION, language 0x{language:X4}) failed for '{exePath}'");
            }

            fixed (byte* data = blob)
            {
                if (!UpdateResourceW(hUpdate, RtVersion, VersionInfoId, LanguageNeutral,
                                     (IntPtr)data, (uint)blob.Length))
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        $"UpdateResource (RT_VERSION) failed for '{exePath}'");
            }

            if (!EndUpdateResourceW(hUpdate, fDiscard: false))
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    $"EndUpdateResource (commit) failed for '{exePath}'");
            committed = true;
        }
        finally
        {
            // EndUpdateResource runs at most once per handle: the commit above, or
            // the discard here on any path that did not reach it.
            if (!committed)
                _ = EndUpdateResourceW(hUpdate, fDiscard: true);
        }
    }

    /// <summary>
    /// The language ids of every <c>RT_VERSION</c> resource with ID 1 in
    /// <paramref name="exePath"/>; empty when the file has none. (R105)
    /// </summary>
    internal static IReadOnlyList<ushort> ReadVersionLanguages(string exePath)
    {
        var languages = new List<ushort>();
        var hModule = LoadLibraryExW(exePath, IntPtr.Zero, LoadLibraryAsDataFile);
        if (hModule == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                $"LoadLibraryEx (datafile) failed for '{exePath}'");

        var handle = GCHandle.Alloc(languages);
        try
        {
            if (!EnumResourceLanguagesW(hModule, RtVersion, VersionInfoId, &CollectLanguage,
                                        GCHandle.ToIntPtr(handle)))
            {
                var error = Marshal.GetLastWin32Error();
                if (error is not (0 or ErrorResourceDataNotFound or ErrorResourceTypeNotFound
                                  or ErrorResourceNameNotFound or ErrorResourceLangNotFound))
                    throw new Win32Exception(error,
                        $"EnumResourceLanguages (RT_VERSION) failed for '{exePath}'");
            }
        }
        finally
        {
            handle.Free();
            _ = FreeLibrary(hModule);
        }
        return languages;
    }

    [UnmanagedCallersOnly]
    private static int CollectLanguage(IntPtr hModule, IntPtr lpType, IntPtr lpName, ushort wLanguage, IntPtr lParam)
    {
        if (GCHandle.FromIntPtr(lParam).Target is List<ushort> list)
            list.Add(wLanguage);
        return 1; // continue enumeration
    }

    /// <summary>
    /// Serializes <paramref name="fields"/> as a complete binary
    /// <c>VS_VERSIONINFO</c> structure (see the class remarks for the layout). (R105)
    /// </summary>
    internal static byte[] Build(VersionResourceFields fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        using var w = new BlockWriter();
        var root = w.BeginBlock("VS_VERSION_INFO", FixedFileInfoSize, type: 0);
        WriteFixedFileInfo(w, fields);

        var stringFileInfo = w.BeginBlock("StringFileInfo", valueLength: 0, type: 1);
        var stringTable = w.BeginBlock(StringTableKey, valueLength: 0, type: 1);
        WriteString(w, "CompanyName", fields.CompanyName);
        WriteString(w, "FileDescription", fields.FileDescription);
        WriteString(w, "FileVersion", fields.FileVersion);
        WriteString(w, "InternalName", fields.InternalName);
        WriteString(w, "LegalCopyright", fields.LegalCopyright);
        WriteString(w, "OriginalFilename", fields.OriginalFilename);
        WriteString(w, "ProductName", fields.ProductName);
        WriteString(w, "ProductVersion", fields.ProductVersion);
        w.EndBlock(stringTable);
        w.EndBlock(stringFileInfo);

        var varFileInfo = w.BeginBlock("VarFileInfo", valueLength: 0, type: 1);
        var translation = w.BeginBlock("Translation", valueLength: 4, type: 0);
        w.WriteUInt16(LanguageNeutral);
        w.WriteUInt16(CodePageUnicode);
        w.EndBlock(translation);
        w.EndBlock(varFileInfo);

        w.EndBlock(root);
        return w.ToArray();
    }

    private static void WriteFixedFileInfo(BlockWriter w, VersionResourceFields f)
    {
        var versionMs = ((uint)f.Major << 16) | f.Minor;
        var versionLs = (uint)f.Patch << 16;
        w.WriteUInt32(FixedFileInfoSignature);
        w.WriteUInt32(FixedFileInfoStrucVersion);
        w.WriteUInt32(versionMs);                // dwFileVersionMS
        w.WriteUInt32(versionLs);                // dwFileVersionLS
        w.WriteUInt32(versionMs);                // dwProductVersionMS
        w.WriteUInt32(versionLs);                // dwProductVersionLS
        w.WriteUInt32(FileFlagsMask);
        w.WriteUInt32(f.IsPrerelease ? FileFlagPrerelease : 0);
        w.WriteUInt32(FileOsNtWindows32);
        w.WriteUInt32(FileTypeApp);
        w.WriteUInt32(0);                        // dwFileSubtype
        w.WriteUInt32(0);                        // dwFileDateMS
        w.WriteUInt32(0);                        // dwFileDateLS
    }

    private static void WriteString(BlockWriter w, string key, string value)
    {
        var text = Sanitize(value);
        var block = w.BeginBlock(key, valueLength: (ushort)(text.Length + 1), type: 1);
        w.WriteUtf16Z(text);
        w.EndBlock(block);
    }

    /// <summary>
    /// Drops embedded NULs (they would end the value early) and cuts the value at
    /// <see cref="MaxValueLength"/> without splitting a surrogate pair.
    /// </summary>
    private static string Sanitize(string? value)
    {
        var text = (value ?? string.Empty).Replace("\0", string.Empty, StringComparison.Ordinal);
        if (text.Length <= MaxValueLength)
            return text;
        var cut = MaxValueLength;
        if (char.IsHighSurrogate(text[cut - 1]))
            cut--;
        return text[..cut];
    }

    /// <summary>
    /// Writes the nested length-prefixed blocks of a version resource: each block
    /// begins 32-bit aligned, and its <c>wLength</c> is patched when it ends.
    /// </summary>
    private sealed class BlockWriter : IDisposable
    {
        private readonly MemoryStream _stream = new();

        public int BeginBlock(string key, ushort valueLength, ushort type)
        {
            Align();
            var start = (int)_stream.Position;
            WriteUInt16(0); // wLength, patched by EndBlock
            WriteUInt16(valueLength);
            WriteUInt16(type);
            WriteUtf16Z(key);
            Align();
            return start;
        }

        public void EndBlock(int start)
        {
            var end = (int)_stream.Position;
            var length = end - start;
            if (length > ushort.MaxValue)
                throw new InvalidOperationException(
                    $"version resource block exceeds {ushort.MaxValue} bytes ({length})");
            var buffer = _stream.GetBuffer();
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(start), (ushort)length);
        }

        public void WriteUInt16(ushort value)
        {
            Span<byte> b = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(b, value);
            _stream.Write(b);
        }

        public void WriteUInt32(uint value)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(b, value);
            _stream.Write(b);
        }

        public void WriteUtf16Z(string text)
        {
            _stream.Write(Encoding.Unicode.GetBytes(text));
            WriteUInt16(0);
        }

        public byte[] ToArray() => _stream.ToArray();

        public void Dispose() => _stream.Dispose();

        private void Align()
        {
            while ((_stream.Position & 3) != 0)
                _stream.WriteByte(0);
        }
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

    [LibraryImport("kernel32.dll", EntryPoint = "LoadLibraryExW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr LoadLibraryExW(string lpLibFileName, IntPtr hFile, uint dwFlags);

    [LibraryImport("kernel32.dll", EntryPoint = "FreeLibrary", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeLibrary(IntPtr hLibModule);

    [LibraryImport("kernel32.dll", EntryPoint = "EnumResourceLanguagesW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumResourceLanguagesW(IntPtr hModule, IntPtr lpType, IntPtr lpName,
        delegate* unmanaged<IntPtr, IntPtr, IntPtr, ushort, IntPtr, int> lpEnumFunc, IntPtr lParam);
}
