namespace SigilBuild.Wrapper.Engine;

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

/// <summary>
/// An open kernel handle to a directory, opened <b>without following a reparse
/// point</b>, together with the facts read from that one handle: whether it really is a
/// plain directory, when it was created, and whether it carries a given marker file.
/// Register row R50.
/// </summary>
/// <remarks>
/// <para>
/// This type exists because "check the path, then act on the path" is not a safe shape
/// for anything destructive. Two path lookups a microsecond apart can name two
/// different objects, and on Windows the cheap way to make that happen needs no
/// privilege at all: a directory junction. A handle pins one kernel object, so every
/// answer below describes the same thing, and the caller can decide about *it* rather
/// than about a name.
/// </para>
/// <para>
/// <c>FILE_FLAG_OPEN_REPARSE_POINT</c> is the load-bearing flag. Without it, opening a
/// junction opens its target, and a check that then says "yes, a plain directory,
/// administrator-owned" would be describing <c>C:\Windows\System32</c> while the caller
/// went on to delete something else entirely. With it, the link itself is opened and
/// <see cref="IsPlainDirectory"/> answers <c>false</c> on its
/// <c>FILE_ATTRIBUTE_REPARSE_POINT</c> bit.
/// </para>
/// <para>
/// <c>FILE_FLAG_BACKUP_SEMANTICS</c> is simply how Win32 permits opening a directory at
/// all; it grants nothing by itself (the privileges it would honour are not held by an
/// ordinary process, and none are needed here).
/// </para>
/// <para>
/// The requested access is <c>READ_CONTROL | FILE_LIST_DIRECTORY</c> — enough to read
/// the security descriptor and enumerate, and deliberately not enough to modify
/// anything. Deletion, when the caller decides on it, happens after this handle is
/// closed.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed partial class DirectoryHandle : IDisposable
{
    private const uint ReadControl = 0x00020000;
    private const uint FileListDirectory = 0x00000001;
    private const uint FileShareAll = 0x00000001 | 0x00000002 | 0x00000004; // read | write | delete
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;

    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeReparsePoint = 0x00000400;

    private readonly uint _attributes;

    private DirectoryHandle(SafeFileHandle handle, uint attributes, long creationFileTimeUtc, bool hasLeaseFile)
    {
        Handle = handle;
        _attributes = attributes;
        CreationTimeUtc = DateTime.FromFileTimeUtc(creationFileTimeUtc);
        HasLeaseFile = hasLeaseFile;
    }

    /// <summary>The live handle, for callers that read more from it (e.g. the DACL).</summary>
    public SafeFileHandle Handle { get; }

    /// <summary>
    /// True only when the opened object is a directory AND is not a reparse point.
    /// Both halves matter: a junction is also flagged as a directory.
    /// </summary>
    public bool IsPlainDirectory =>
        (_attributes & FileAttributeDirectory) != 0
        && (_attributes & FileAttributeReparsePoint) == 0;

    /// <summary>Creation time, read from the handle rather than re-statted by path.</summary>
    public DateTime CreationTimeUtc { get; }

    /// <summary>Whether the marker file the caller asked about was present at open time.</summary>
    public bool HasLeaseFile { get; }

    /// <summary>
    /// Open <paramref name="directory"/> without following a reparse point at its final
    /// component, or return <c>null</c> when it cannot be opened at all (it vanished,
    /// or this process may not read it). A <c>null</c> is always "leave it alone",
    /// never "proceed".
    /// </summary>
    /// <param name="directory">The directory to open.</param>
    /// <param name="markerFileName">
    /// A file name whose presence inside <paramref name="directory"/> is recorded in
    /// <see cref="HasLeaseFile"/>.
    /// </param>
    public static DirectoryHandle? OpenNoFollow(string directory, string markerFileName)
    {
        var handle = CreateFileW(
            directory,
            ReadControl | FileListDirectory,
            FileShareAll,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        if (!GetFileInformationByHandle(handle, out var info))
        {
            handle.Dispose();
            return null;
        }

        // Probed through the path, deliberately and harmlessly: its ONLY effect is to
        // make an unleased directory wait out the grace period, i.e. it can delay a
        // reclaim but never authorize one. Every answer that could authorize a delete
        // comes from the handle.
        bool hasMarker;
#pragma warning disable CA1031 // Unreadable means "assume no marker", which is the slower, safer branch.
        try
        {
            hasMarker = File.Exists(Path.Combine(directory, markerFileName));
        }
        catch
        {
            hasMarker = false;
        }
#pragma warning restore CA1031

        var creation = ((long)info.CreationTimeHigh << 32) | info.CreationTimeLow;
        return new DirectoryHandle(handle, info.FileAttributes, creation, hasMarker);
    }

    public void Dispose() => Handle.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(
        SafeFileHandle hFile, out ByHandleFileInformation lpFileInformation);
}
