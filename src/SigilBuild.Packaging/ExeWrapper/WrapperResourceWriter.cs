using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SigilBuild.Packaging.ExeWrapper;

/// <summary>
/// Embeds the step blob and payload into the AOT-published wrapper runtime
/// as Win32 binary resources.
/// </summary>
/// <remarks>
/// Uses the Win32 <c>BeginUpdateResource</c> / <c>UpdateResource</c> /
/// <c>EndUpdateResource</c> flow to attach two custom <c>RT_RCDATA</c>
/// resource blobs to the freshly-copied <c>SigilBuild.Wrapper.exe</c>:
/// <list type="bullet">
///   <item>
///     <c>SIGIL_BLOB_V1</c> — the JSON-serialised step + parameter blob.
///   </item>
///   <item>
///     <c>SIGIL_PAYLOAD_V1</c> — the user payload bytes (zip archive of the
///     manifest's <c>SourceDirectory</c> for now; richer payload-extraction
///     story lands with Tasks 15+).
///   </item>
/// </list>
/// Both resources are read at install time by the wrapper runtime via
/// <see cref="SigilBuild.Wrapper.Engine.WrapperBlob.LoadFromSelf"/>. Because
/// the underlying Win32 calls are not async-friendly the implementation is
/// fully synchronous and exposed as a <see cref="Task"/>-returning method via
/// <see cref="Task.FromResult{TResult}(TResult)"/>; this matches the
/// <see cref="IPackager"/> convention without forcing a dedicated
/// thread-pool hop. The host build (<c>SigilBuild.Cli</c>) that invokes this
/// writer must run on Windows because <c>UpdateResource</c> is a Win32 API;
/// non-Windows hosts surface a diagnostic from
/// <see cref="ExeWrapperPackager"/> before reaching here.
/// </remarks>
internal static partial class WrapperResourceWriter
{
    // RT_RCDATA — application-defined raw data resource (winuser.h).
    private static readonly IntPtr RtRcData = (IntPtr)10;

    // LANG_NEUTRAL, SUBLANG_NEUTRAL — primary language id 0.
    private const ushort LangNeutral = 0;

    private const string BlobResourceName = "SIGIL_BLOB_V1";
    private const string PayloadResourceName = "SIGIL_PAYLOAD_V1";

    public static Task WriteAsync(string exePath, byte[] blob, byte[] payload, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(exePath);
        ArgumentNullException.ThrowIfNull(blob);
        ArgumentNullException.ThrowIfNull(payload);
        ct.ThrowIfCancellationRequested();

        WriteCore(exePath, blob, payload);
        return Task.CompletedTask;
    }

    private static void WriteCore(string exePath, byte[] blob, byte[] payload)
    {
        // BeginUpdateResource(deleteExistingResources: false) keeps the AOT
        // binary's pre-existing resources (manifest, version info, icon)
        // intact and only adds/overrides the named resources we touch.
        var hUpdate = BeginUpdateResourceW(exePath, deleteExistingResources: false);
        if (hUpdate == IntPtr.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"BeginUpdateResource failed for '{exePath}'");
        }

        var blobNamePtr = IntPtr.Zero;
        var payloadNamePtr = IntPtr.Zero;
        var committed = false;
        try
        {
            blobNamePtr = Marshal.StringToHGlobalUni(BlobResourceName);
            payloadNamePtr = Marshal.StringToHGlobalUni(PayloadResourceName);

            UpdateOne(hUpdate, blobNamePtr, blob, BlobResourceName);
            UpdateOne(hUpdate, payloadNamePtr, payload, PayloadResourceName);

            // Commit — fDiscard: false writes the changes to disk.
            if (!EndUpdateResourceW(hUpdate, fDiscard: false))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"EndUpdateResource (commit) failed for '{exePath}'");
            }
            committed = true;
        }
        finally
        {
            if (blobNamePtr != IntPtr.Zero) Marshal.FreeHGlobal(blobNamePtr);
            if (payloadNamePtr != IntPtr.Zero) Marshal.FreeHGlobal(payloadNamePtr);

            // If we threw before commit, discard the in-memory update so the
            // exe on disk is unchanged. Ignore the bool result — we are
            // already unwinding.
            if (!committed)
            {
                _ = EndUpdateResourceW(hUpdate, fDiscard: true);
            }
        }
    }

    private static void UpdateOne(IntPtr hUpdate, IntPtr namePtr, byte[] data, string nameForDiag)
    {
        // UpdateResource accepts data via a raw pointer + byte count; we pin
        // the managed array for the duration of the call.
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var ok = UpdateResourceW(
                hUpdate,
                RtRcData,
                namePtr,
                LangNeutral,
                handle.AddrOfPinnedObject(),
                (uint)data.Length);
            if (!ok)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"UpdateResource failed for '{nameForDiag}'");
            }
        }
        finally
        {
            handle.Free();
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "BeginUpdateResourceW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.SysInt)]
    private static partial IntPtr BeginUpdateResourceW(
        string pFileName,
        [MarshalAs(UnmanagedType.Bool)] bool deleteExistingResources);

    [LibraryImport("kernel32.dll", EntryPoint = "UpdateResourceW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateResourceW(
        IntPtr hUpdate,
        IntPtr lpType,
        IntPtr lpName,
        ushort wLanguage,
        IntPtr lpData,
        uint cbData);

    [LibraryImport("kernel32.dll", EntryPoint = "EndUpdateResourceW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EndUpdateResourceW(
        IntPtr hUpdate,
        [MarshalAs(UnmanagedType.Bool)] bool fDiscard);
}
