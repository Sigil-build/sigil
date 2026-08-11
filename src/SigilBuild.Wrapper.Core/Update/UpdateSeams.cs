using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SigilBuild.Wrapper.Engine;

namespace SigilBuild.Wrapper.Update;

// The three I/O boundaries of the /Update runtime (T12.3), factored behind small
// seams so the fetch → verify → compare → download → run-child decision logic in
// UpdateRunner is unit-testable with plain test doubles (no mocking framework) and
// no real network / child process. The production implementations here are the
// CI-VM-only live legs (their end-to-end coverage is T12.6's job).

/// <summary>Fetches the raw bytes of an HTTP(S) resource — the channel manifest and its detached <c>.sig</c>.</summary>
internal interface IUpdateResourceFetcher
{
    Task<UpdateResourceResult> FetchAsync(string url, CancellationToken ct);
}

/// <summary>Typed outcome of a resource fetch. The verification is over the exact bytes, so the raw body is carried, not text.</summary>
internal readonly record struct UpdateResourceResult(bool Success, byte[]? Bytes, string? Error)
{
    public static UpdateResourceResult Ok(byte[] bytes) => new(true, bytes, null);

    public static UpdateResourceResult Failed(string error) => new(false, null, error);
}

/// <summary>Downloads + verifies the update package to a destination file (sha256 mandatory).</summary>
internal interface IUpdatePackageDownloader
{
    Task<UpdatePackageDownloadResult> DownloadAsync(string url, string destination, string sha256, CancellationToken ct);
}

/// <summary>Typed outcome of a verified package download.</summary>
internal readonly record struct UpdatePackageDownloadResult(bool Success, string? Error)
{
    public static UpdatePackageDownloadResult Ok() => new(true, null);

    public static UpdatePackageDownloadResult Failed(string error) => new(false, error);
}

/// <summary>Runs the downloaded child Setup.exe to completion and returns its exit code.</summary>
internal interface IChildInstallerLauncher
{
    Task<int> RunAsync(string exePath, IReadOnlyList<string> args, CancellationToken ct);
}

/// <summary>
/// Production fetcher over the one shared <see cref="SigilHttpClient"/> (HTTPS,
/// system proxy). A per-request timeout is applied via a linked CTS; a non-success
/// status or a transport error becomes a typed failure rather than an exception, so
/// the runner maps "could not check for updates" onto a clean exit code. Genuine
/// user cancellation still propagates.
/// </summary>
internal sealed class HttpUpdateResourceFetcher : IUpdateResourceFetcher
{
    /// <summary>
    /// Ceiling on a channel-manifest / detached-signature body: 256 KiB.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>THIS BUFFER IS PRE-AUTHENTICATION.</b> It is filled, in full, in memory,
    /// before <c>ChannelManifestVerifier.Verify</c> has said one word about the bytes
    /// (<c>UpdateRunner.RunAsync</c> fetches at step 2 and verifies at step 4). Every
    /// byte accepted here is therefore accepted on the say-so of whoever answered the
    /// request — a hostile origin, a proxy, or anyone holding the DNS name. That makes
    /// an unbounded read here a memory-exhaustion primitive reachable by an
    /// unauthenticated party, which is register row R10's actual point, and it is why
    /// this cap is three orders of magnitude below
    /// <see cref="SigilDownloader.DefaultMaxBytes"/> rather than merely smaller.
    /// </para>
    /// <para>
    /// The signature verification cannot be moved earlier: it is computed over the
    /// exact fetched bytes, so the bytes have to exist first. Bounding how many of them
    /// may exist is the only lever, and 256 KiB is roughly two orders of magnitude above
    /// a real channel manifest (a few hundred bytes of JSON) and above its base64
    /// signature.
    /// </para>
    /// </remarks>
    internal const int MaxResourceBytes = 256 * 1024;

    private readonly TimeSpan _timeout;

    public HttpUpdateResourceFetcher(TimeSpan timeout) => _timeout = timeout;

    public async Task<UpdateResourceResult> FetchAsync(string url, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_timeout);
        try
        {
            // ResponseHeadersRead, not ResponseContentRead: the latter buffers the whole
            // body inside HttpClient before this method regains control, which is the
            // unbounded pre-authentication read being closed. Headers first, then a
            // metered copy.
            using var resp = await SigilHttpClient.Shared
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return UpdateResourceResult.Failed($"HTTP {(int)resp.StatusCode} fetching '{url}'");
            }

            if (resp.Content.Headers.ContentLength is long declared && declared > MaxResourceBytes)
            {
                return UpdateResourceResult.Failed(
                    $"'{url}' declared {declared} bytes, above the {MaxResourceBytes}-byte ceiling for an " +
                    "update resource — refusing to buffer an unverified body that large");
            }

            var bytes = await ReadBoundedAsync(resp, timeoutCts.Token).ConfigureAwait(false);
            return bytes is null
                ? UpdateResourceResult.Failed(
                    $"'{url}' streamed more than the {MaxResourceBytes}-byte ceiling for an update resource — " +
                    "refusing to buffer an unverified body that large")
                : UpdateResourceResult.Ok(bytes);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // genuine user cancel — propagate.
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            // OperationCanceledException here (with ct not cancelled) is the per-request
            // timeout — a transient "could not check", not a user cancel.
            return UpdateResourceResult.Failed($"could not fetch '{url}': {ex.Message}");
        }
    }

    /// <summary>
    /// Copy at most <see cref="MaxResourceBytes"/> bytes out of the response, returning
    /// <c>null</c> the instant the body proves to be longer. Metered rather than
    /// buffered, because a response framed by connection-close declares no
    /// <c>Content-Length</c> at all — the up-front check above never fires for it, and
    /// the per-request timeout does not bound it either, since a body that keeps
    /// arriving keeps the request alive.
    /// </summary>
    private static async Task<byte[]?> ReadBoundedAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (src.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream(capacity: 4096);
            var chunk = new byte[8192];
            int n;
            while ((n = await src.ReadAsync(chunk.AsMemory(0, chunk.Length), ct).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + n > MaxResourceBytes)
                {
                    return null;
                }
                buffer.Write(chunk, 0, n);
            }
            return buffer.ToArray();
        }
    }
}

/// <summary>Production downloader delegating to the shared P4 <see cref="SigilDownloader"/> (retry + sha256 verify).</summary>
internal sealed class SigilPackageDownloader : IUpdatePackageDownloader
{
    private readonly TimeSpan _timeout;
    private readonly int _maxAttempts;
    private readonly Action<string, bool>? _report;

    public SigilPackageDownloader(TimeSpan timeout, int maxAttempts, Action<string, bool>? report)
    {
        _timeout = timeout;
        _maxAttempts = maxAttempts;
        _report = report;
    }

    public async Task<UpdatePackageDownloadResult> DownloadAsync(
        string url, string destination, string sha256, CancellationToken ct)
    {
        // R10: the update package is bounded by the absolute file-download backstop. Its
        // sha256 comes from a SIGNED channel manifest, so its integrity is authenticated —
        // but the size is not declared anywhere, and the bytes arrive before the hash can
        // say anything, so an unbounded transfer is still an unbounded transfer.
        var result = await SigilDownloader
            .DownloadVerifiedAsync(
                url, destination, sha256, _timeout, _maxAttempts, SigilDownloader.DefaultMaxBytes, _report, ct)
            .ConfigureAwait(false);
        return result.Success
            ? UpdatePackageDownloadResult.Ok()
            : UpdatePackageDownloadResult.Failed(result.Error ?? "download failed");
    }
}

/// <summary>
/// Production child-process launcher: spawn the downloaded Setup.exe with no window
/// and wait for it, so its exit code (the P3 upgrade's own result — 0 / 3010 / a
/// failure code) can be propagated as this process's exit code.
/// </summary>
internal sealed class ProcessChildInstallerLauncher : IChildInstallerLauncher
{
    public async Task<int> RunAsync(string exePath, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"could not start '{exePath}'");
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        return proc.ExitCode;
    }
}
