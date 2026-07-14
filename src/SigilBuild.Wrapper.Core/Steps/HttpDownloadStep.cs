namespace SigilBuild.Wrapper.Steps;

using System;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;

/// <summary>
/// Install-time <c>http_download</c> step (P4, gap G5): streams an HTTPS URL to a
/// destination file, verifies its SHA-256, and journals the write so a rollback
/// deletes the download. HTTPS-only; the system proxy is honored (shared
/// <see cref="SigilHttpClient"/>). Transient failures (network / timeout / 5xx)
/// are retried with exponential backoff; a checksum mismatch is not transient and
/// fails immediately. No resume — a retry restarts the download.
/// </summary>
internal sealed class HttpDownloadStep : IStep
{
    private const int DefaultTimeoutSeconds = 300;

    private readonly InstallStep.HttpDownload _spec;

    public HttpDownloadStep(InstallStep.HttpDownload spec) => _spec = spec;

    public async Task<StepResult> RunAsync(StepContext ctx, RollbackJournal journal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(journal);

        // {var.*}/{install_dir} tokens in url + dest.
        var url = ctx.Resolve(_spec.Url);
        var dest = ctx.ResolvePath(_spec.Dest);

        // Defense-in-depth: pack-time rejects http://, but a token-built URL is
        // re-checked here at run time.
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return new StepResult(false, $"http_download url must be https:// (got '{url}')");
        }

        var expected = (_spec.Sha256 ?? string.Empty).Trim();
        if (expected.Length == 0)
        {
            return new StepResult(false, "http_download requires a sha256 checksum");
        }

        var timeout = _spec.TimeoutSeconds is int t and > 0
            ? TimeSpan.FromSeconds(t)
            : TimeSpan.FromSeconds(DefaultTimeoutSeconds);
        var maxAttempts = 1 + (_spec.Retries is int r and > 0 ? r : 0);

        var dir = Path.GetDirectoryName(dest);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // Journal the write BEFORE downloading so a crash mid-download still cleans
        // up. If the destination already exists, back it up so rollback restores it;
        // otherwise rollback deletes whatever we create.
        var existedBefore = File.Exists(dest);
        string? backup = null;
        if (existedBefore)
        {
            backup = dest + ".sigil-bak";
            File.Copy(dest, backup, overwrite: true);
        }
        journal.Append(new RollbackRecord.RestoreFile(dest, existedBefore, backup));

        Exception? lastTransient = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var actual = await DownloadAndHashAsync(url, dest, timeout, ctx, ct).ConfigureAwait(false);
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    // A wrong/corrupt file won't heal on retry — fail now (engine rolls back).
                    return new StepResult(false,
                        $"sha256 mismatch for '{url}': expected {expected}, got {actual}");
                }
                Report(ctx, $"download: verified {Path.GetFileName(dest)}", isError: false);
                return new StepResult(true, null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // genuine user cancel — propagate to the engine's rollback.
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                lastTransient = ex;
                if (attempt < maxAttempts)
                {
                    var backoff = TimeSpan.FromMilliseconds(500 * (1 << (attempt - 1)));
                    Report(ctx, $"download: attempt {attempt} failed ({ex.Message}); retrying in {backoff.TotalSeconds:0.#}s", isError: true);
                    await Task.Delay(backoff, ct).ConfigureAwait(false);
                }
            }
#pragma warning disable CA1031 // Non-transient failures are surfaced as a typed StepResult (engine rolls back).
            catch (Exception ex)
            {
                return new StepResult(false, $"download of '{url}' failed: {ex.Message}");
            }
#pragma warning restore CA1031
        }

        return new StepResult(false,
            $"download of '{url}' failed after {maxAttempts} attempt(s): {lastTransient?.Message ?? "unknown error"}");
    }

    private static async Task<string> DownloadAndHashAsync(
        string url, string dest, TimeSpan timeout, StepContext ctx, CancellationToken ct)
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
        Report(ctx, $"download: {name} …", isError: false);

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
                            Report(ctx, $"download: {name} {pct}%", isError: false);
                        }
                    }
                }
            }
        }

        return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Report(StepContext ctx, string message, bool isError)
        => ctx.ProgressSink?.Report(new StepProgress(0, 0, message, isError));

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
