namespace SigilBuild.Wrapper.Engine;

using System;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Shared, AOT-safe verified-download helper: streams an HTTPS URL to a destination
/// file, verifies its SHA-256, and retries transient failures (network / timeout /
/// 5xx) with exponential backoff. Reused by the <c>http_download</c> step (P4) and
/// the prerequisite runner (P5) over the one <see cref="SigilHttpClient"/> (system
/// proxy honored). No journaling and no resume — the caller owns rollback / cleanup;
/// a retry restarts the download. A checksum mismatch is not transient and returns
/// immediately.
/// </summary>
public static class SigilDownloader
{
    /// <summary>
    /// The ceiling a downloaded <em>file</em> (an install payload, a prerequisite
    /// installer, an update package) may not exceed: 2 GiB. Register row R10 —
    /// <c>Content-Length</c> was read for a progress percentage and never enforced,
    /// and the read loop had no cap at all, so a hostile or compromised origin could
    /// drip an unbounded body into the destination. A per-request timeout does not
    /// bound that: a slow-drip body resets nothing but the clock on each read.
    /// </summary>
    /// <remarks>
    /// This is an absolute backstop, not a policy: it is deliberately far above any
    /// legitimate redistributable or stamped <c>Setup.exe</c> so that raising it is
    /// never the fix for a real package. The load-bearing cap is
    /// <see cref="Update.HttpUpdateResourceFetcher"/>'s, which is three orders of
    /// magnitude smaller because that buffer is filled BEFORE anything about its
    /// contents has been authenticated.
    /// </remarks>
    public const long DefaultMaxBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>The classification of a verified-download attempt.</summary>
    public enum DownloadStatus
    {
        /// <summary>Downloaded and the SHA-256 matched.</summary>
        Ok,

        /// <summary>Downloaded but the SHA-256 did not match the expected value.</summary>
        ChecksumMismatch,

        /// <summary>The download itself failed (after any retries).</summary>
        Failed,

        /// <summary>
        /// The response declared, or streamed, more than the caller's <c>maxBytes</c>
        /// ceiling. Never retried — a body that is too big does not shrink.
        /// </summary>
        TooLarge,
    }

    /// <summary>Result of <see cref="DownloadVerifiedAsync"/>.</summary>
    public readonly record struct DownloadResult(DownloadStatus Status, string? ActualSha256, string? Error)
    {
        public bool Success => Status == DownloadStatus.Ok;
    }

    /// <summary>
    /// Download <paramref name="url"/> to <paramref name="dest"/> and verify the
    /// SHA-256 against <paramref name="expectedSha256"/> (hex, case-insensitive).
    /// Retries transient failures up to <paramref name="maxAttempts"/> total attempts.
    /// <paramref name="report"/> receives progress / retry lines (message, isError).
    /// Genuine user cancellation propagates as <see cref="OperationCanceledException"/>.
    /// </summary>
    /// <param name="maxBytes">
    /// Hard ceiling on the response body, in bytes. Enforced TWICE and both halves
    /// matter (register row R10): a declared <c>Content-Length</c> above it is refused
    /// before a single body byte is read or the destination file is created — cheap,
    /// and it costs the attacker nothing to avoid — and the read loop itself aborts the
    /// moment the transferred count would pass it, which is the defence that actually
    /// holds against a server that declares nothing (or lies) and drips. Required, and
    /// deliberately not defaulted: a new download site must state its own ceiling
    /// rather than inherit one silently. <see cref="DefaultMaxBytes"/> is the
    /// file-download backstop.
    /// </param>
    public static async Task<DownloadResult> DownloadVerifiedAsync(
        string url,
        string dest,
        string expectedSha256,
        TimeSpan timeout,
        int maxAttempts,
        long maxBytes,
        Action<string, bool>? report,
        CancellationToken ct)
    {
        var expected = (expectedSha256 ?? string.Empty).Trim();
        if (maxAttempts < 1)
        {
            maxAttempts = 1;
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        Exception? lastTransient = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var actual = await DownloadAndHashAsync(url, dest, timeout, maxBytes, report, ct).ConfigureAwait(false);
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    // A wrong/corrupt file won't heal on retry — fail now.
                    return new DownloadResult(DownloadStatus.ChecksumMismatch, actual,
                        $"sha256 mismatch for '{url}': expected {expected}, got {actual}");
                }
                return new DownloadResult(DownloadStatus.Ok, actual, null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // genuine user cancel — propagate.
            }
            catch (DownloadTooLargeException ex)
            {
                // Caught BEFORE the transient filter on purpose. An oversized body is
                // permanent — retrying re-fetches the same body from the same origin and
                // is exactly the amplification an attacker would want.
                return new DownloadResult(DownloadStatus.TooLarge, null, $"download of '{url}' refused: {ex.Message}");
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                lastTransient = ex;
                if (attempt < maxAttempts)
                {
                    var backoff = TimeSpan.FromMilliseconds(500 * (1 << (attempt - 1)));
                    report?.Invoke($"download: attempt {attempt} failed ({ex.Message}); retrying in {backoff.TotalSeconds:0.#}s", true);
                    await Task.Delay(backoff, ct).ConfigureAwait(false);
                }
            }
#pragma warning disable CA1031 // Non-transient failures are surfaced as a typed result.
            catch (Exception ex)
            {
                return new DownloadResult(DownloadStatus.Failed, null, $"download of '{url}' failed: {ex.Message}");
            }
#pragma warning restore CA1031
        }

        return new DownloadResult(DownloadStatus.Failed, null,
            $"download of '{url}' failed after {maxAttempts} attempt(s): {lastTransient?.Message ?? "unknown error"}");
    }

    private static async Task<string> DownloadAndHashAsync(
        string url, string dest, TimeSpan timeout, long maxBytes, Action<string, bool>? report, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var token = timeoutCts.Token;

        using var resp = await SigilHttpClient.Shared
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new DownloadException((int)resp.StatusCode);
        }

        var total = resp.Content.Headers.ContentLength;

        // R10, first half: refuse a declared oversize BEFORE the body stream is opened
        // and before the destination file is created — so a hostile Content-Length costs
        // this process one request and nothing on disk. Cheap and honest, but it is only
        // the half an attacker can trivially skip by declaring nothing; the loop below is
        // the half that holds.
        if (total is long declared && declared > maxBytes)
        {
            throw new DownloadTooLargeException(
                $"the server declared a body of {declared} bytes, above the {maxBytes}-byte ceiling for this download");
        }

        var name = Path.GetFileName(dest);
        report?.Invoke($"download: {name} …", false);

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var src = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using (src.ConfigureAwait(false))
        {
            // R5's residual: never write THROUGH whatever already holds this name.
            // FileMode.Create opens an existing entry — including a hardlink or a file
            // reparse point an attacker planted at a predictable destination — and
            // truncates its TARGET, which from an elevated process is an arbitrary-file
            // -write primitive. Deleting the name first drops the attacker's link (a
            // hardlink's other names, and a junction's target, are left alone), and
            // CreateNew then makes a brand-new file inheriting the directory's DACL.
            // A directory or directory junction squatting the name survives the delete
            // and makes CreateNew throw, which is the correct loud failure; so does
            // anything that re-creates the name in the gap. Overwriting a genuine
            // pre-existing file still works — HttpDownloadStep has already journaled a
            // backup of it by this point.
            DeleteDestinationName(dest);
            var file = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await using (file.ConfigureAwait(false))
            {
                var buffer = new byte[81920];
                long read = 0;
                var lastPct = -1;
                int n;
                while ((n = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false)) > 0)
                {
                    // R10, second half — the one that matters. Checked BEFORE the write, so
                    // not one byte past the ceiling ever reaches the disk. A response framed
                    // by connection-close carries no Content-Length at all, so the check
                    // above never fires for it and this is the only thing standing between a
                    // slow-drip origin and an unbounded file; the per-request timeout is not
                    // that thing, because a body that keeps arriving keeps the request alive.
                    if (read + n > maxBytes)
                    {
                        throw new DownloadTooLargeException(
                            $"the response exceeded the {maxBytes}-byte ceiling for this download " +
                            $"(stopped after {read} bytes; the server declared " +
                            $"{(total is long t2 ? t2.ToString(System.Globalization.CultureInfo.InvariantCulture) : "no Content-Length")})");
                    }

                    await file.WriteAsync(buffer.AsMemory(0, n), token).ConfigureAwait(false);
                    hasher.AppendData(buffer, 0, n);
                    read += n;
                    if (total is long tt && tt > 0)
                    {
                        var pct = (int)(read * 100 / tt);
                        if (pct != lastPct && pct % 10 == 0)
                        {
                            lastPct = pct;
                            report?.Invoke($"download: {name} {pct}%", false);
                        }
                    }
                }
            }
        }

        return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>
    /// Remove whatever currently answers to <paramref name="dest"/>, so the download
    /// creates a file rather than opening someone else's. <see cref="File.Delete"/>
    /// unlinks the <em>name</em>: a hardlink's other names and a symlink's target are
    /// untouched, which is exactly the wanted behaviour — the attacker's alias goes
    /// away, the file it pointed at does not. A directory (or a directory junction
    /// posing as one) is deliberately not removed: <see cref="File.Exists"/> answers
    /// false for it, and the subsequent <see cref="FileMode.CreateNew"/> then fails
    /// loudly instead of this method quietly deleting a tree.
    /// </summary>
    private static void DeleteDestinationName(string dest)
    {
        if (File.Exists(dest))
        {
            File.Delete(dest);
        }
    }

    // Only network/timeout/5xx are worth retrying; a 4xx or a checksum mismatch is
    // a permanent condition.
    private static bool IsTransient(Exception ex) => ex switch
    {
        DownloadException d => d.Transient,
        OperationCanceledException => true, // a timeout (user-cancel is handled first)
        HttpRequestException => true,
        IOException => true,
        SocketException => true,
        _ => false,
    };

    private sealed class DownloadException : Exception
    {
        public bool Transient { get; }

        public DownloadException(int statusCode) : base($"HTTP {statusCode}")
            => Transient = statusCode is 408 or 429 || statusCode >= 500;
    }

    /// <summary>
    /// The response declared or streamed more than the caller's ceiling. Deliberately
    /// derived from <see cref="Exception"/> and NOT from <see cref="IOException"/>,
    /// which <see cref="IsTransient"/> retries: an oversized body is a permanent
    /// property of the origin, and retrying it would multiply the very transfer being
    /// refused.
    /// </summary>
    private sealed class DownloadTooLargeException : Exception
    {
        public DownloadTooLargeException(string message) : base(message)
        {
        }
    }
}
