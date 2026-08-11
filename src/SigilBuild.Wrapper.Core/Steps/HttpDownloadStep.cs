namespace SigilBuild.Wrapper.Steps;

using System;
using System.IO;
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

        // R16: contain the download destination before anything is created. The
        // web-installer stub legitimately downloads outside install_dir — it lands
        // in the per-run staging directory, which resolves from its own token, so
        // the token check passes and the containment check is the one the stub's
        // synthesized step opts out of.
        var refusal = StepDestinationGuard.Check(
            ctx.InstallDir, "http_download", "dest", dest, _spec.AllowOutsideInstallDir);
        if (refusal is not null)
        {
            return new StepResult(false, refusal);
        }

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

        // Shared verified-download plumbing (P4/P5): retry + hash-verify over the one
        // proxy-aware HttpClient. This step owns the journaling above; the helper owns
        // the transfer. A genuine user cancel propagates for the engine's rollback.
        var result = await SigilDownloader.DownloadVerifiedAsync(
            url, dest, expected, timeout, maxAttempts,
            report: (msg, isErr) => ctx.ProgressSink?.Report(new StepProgress(0, 0, msg, isErr)),
            ct).ConfigureAwait(false);

        if (result.Success)
        {
            ctx.ProgressSink?.Report(new StepProgress(0, 0, $"download: verified {Path.GetFileName(dest)}", false));
            return new StepResult(true, null);
        }

        // ChecksumMismatch / Failed → typed StepResult; the engine rolls back the write.
        return new StepResult(false, result.Error);
    }
}
