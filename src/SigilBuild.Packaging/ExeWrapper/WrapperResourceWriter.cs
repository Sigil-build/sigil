using System;
using System.Threading;
using System.Threading.Tasks;

namespace SigilBuild.Packaging.ExeWrapper;

/// <summary>
/// Embeds the step blob and payload into the AOT-published wrapper runtime
/// as Win32 binary resources.
/// </summary>
/// <remarks>
/// Stub for Task 7. The full implementation in Task 14 will use the Win32
/// <c>BeginUpdateResource</c> / <c>UpdateResource</c> / <c>EndUpdateResource</c>
/// flow to attach two custom resource blobs to <c>SigilBuild.Wrapper.exe</c>:
/// <list type="bullet">
///   <item>
///     <c>SIGIL_STEPS</c> — the serialized install-step program produced by the
///     planner (binary CBOR / MessagePack TBD; see ADR-008).
///   </item>
///   <item>
///     <c>SIGIL_PAYLOAD</c> — the zstd-compressed payload archive containing the
///     application files to be unpacked at install time.
///   </item>
/// </list>
/// Both resources are read at install time by the wrapper runtime via
/// <c>FindResource</c> + <c>LoadResource</c>. The host build (`SigilBuild.Cli`)
/// that invokes this writer must run on Windows because
/// <c>UpdateResource</c> is a Win32 API; non-Windows hosts will surface a
/// diagnostic from <see cref="ExeWrapperPackager"/> before reaching here.
/// </remarks>
internal static class WrapperResourceWriter
{
    public static Task WriteAsync(string exePath, byte[] blob, byte[] payload, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(exePath);
        ArgumentNullException.ThrowIfNull(blob);
        ArgumentNullException.ThrowIfNull(payload);
        ct.ThrowIfCancellationRequested();
        throw new NotImplementedException("see Task 14");
    }
}
