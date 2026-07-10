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
///     <c>SIGIL_PAYLOAD_V2</c> — the user payload bytes as the deterministic
///     zstd container of the manifest's <c>SourceDirectory</c> (T6, see
///     <c>SigilBuild.Wrapper.Codec.PayloadCodec</c>).
///   </item>
///   <item>
///     <c>SIGIL_RUNTIME_V1</c> — (T18) the host's native dependencies
///     (Skia/ANGLE/HarfBuzz) as a deterministic zip, embedded only when native
///     deps were staged, so a standalone stamped <c>Setup.exe</c> can extract
///     and load them before the GUI wizard starts.
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

    // T6: bumped from SIGIL_PAYLOAD_V1 (deterministic Deflate zip) to
    // SIGIL_PAYLOAD_V2 (deterministic zstd container, see PayloadCodec). The
    // decode side (WrapperBlob.LoadPayloadBytes / PayloadExtraction) is gated on
    // this exact marker, so a V1 blob is treated as "no payload" rather than
    // mis-parsed.
    private const string PayloadResourceName = "SIGIL_PAYLOAD_V2";

    // T18: the host's native dependencies (Skia/ANGLE/HarfBuzz) archived so a
    // standalone stamped Setup.exe can extract + load them before the GUI starts.
    private const string RuntimeResourceName = "SIGIL_RUNTIME_V1";

    public static Task WriteAsync(
        string exePath, byte[] blob, byte[] payload, byte[] runtime, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(exePath);
        ArgumentNullException.ThrowIfNull(blob);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(runtime);
        ct.ThrowIfCancellationRequested();

        WriteCore(exePath, blob, payload, runtime);
        return Task.CompletedTask;
    }

    private static void WriteCore(string exePath, byte[] blob, byte[] payload, byte[] runtime)
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
        var runtimeNamePtr = IntPtr.Zero;
        var committed = false;
        try
        {
            blobNamePtr = Marshal.StringToHGlobalUni(BlobResourceName);
            UpdateOne(hUpdate, blobNamePtr, blob, BlobResourceName);

            // UpdateResource with cbData=0 means "delete the named resource". A
            // freshly-copied wrapper has no SIGIL_PAYLOAD_V1 yet, so the delete
            // returns failure. Skip the call entirely when there is no payload.
            if (payload.Length > 0)
            {
                payloadNamePtr = Marshal.StringToHGlobalUni(PayloadResourceName);
                UpdateOne(hUpdate, payloadNamePtr, payload, PayloadResourceName);
            }

            // T18: only stamp the native-runtime resource when there is one to
            // stamp. An empty archive (no native deps staged) leaves SIGIL_RUNTIME_V1
            // absent, so the host bootstrap correctly no-ops as an un-stamped run.
            if (runtime.Length > 0)
            {
                runtimeNamePtr = Marshal.StringToHGlobalUni(RuntimeResourceName);
                UpdateOne(hUpdate, runtimeNamePtr, runtime, RuntimeResourceName);
            }

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
            if (runtimeNamePtr != IntPtr.Zero) Marshal.FreeHGlobal(runtimeNamePtr);

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
