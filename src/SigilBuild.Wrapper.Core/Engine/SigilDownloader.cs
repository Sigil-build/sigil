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
    /// <summary>The classification of a verified-download attempt.</summary>
    public enum DownloadStatus
    {
        /// <summary>Downloaded and the SHA-256 matched.</summary>
        Ok,

        /// <summary>Downloaded but the SHA-256 did not match the expected value.</summary>
        ChecksumMismatch,

        /// <summary>The download itself failed (after any retries).</summary>
        Failed,
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
    public static async Task<DownloadResult> DownloadVerifiedAsync(
        string url,
        string dest,
        string expectedSha256,
        TimeSpan timeout,
        int maxAttempts,
        Action<string, bool>? report,
        CancellationToken ct)
    {
        var expected = (expectedSha256 ?? string.Empty).Trim();
        if (maxAttempts < 1)
        {
            maxAttempts = 1;
        }

        Exception? lastTransient = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var actual = await DownloadAndHashAsync(url, dest, timeout, report, ct).ConfigureAwait(false);
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
        string url, string dest, TimeSpan timeout, Action<string, bool>? report, CancellationToken ct)
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
        var name = Path.GetFileName(dest);
        report?.Invoke($"download: {name} …", false);

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var src = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using (src.ConfigureAwait(false))
        {
            var file = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
            await using (file.ConfigureAwait(false))
            {
                var buffer = new byte[81920];
                long read = 0;
                var lastPct = -1;
                int n;
                while ((n = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false)) > 0)
                {
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
}
