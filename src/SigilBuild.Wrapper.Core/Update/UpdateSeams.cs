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
    private readonly TimeSpan _timeout;

    public HttpUpdateResourceFetcher(TimeSpan timeout) => _timeout = timeout;

    public async Task<UpdateResourceResult> FetchAsync(string url, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_timeout);
        try
        {
            using var resp = await SigilHttpClient.Shared
                .GetAsync(url, HttpCompletionOption.ResponseContentRead, timeoutCts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return UpdateResourceResult.Failed($"HTTP {(int)resp.StatusCode} fetching '{url}'");
            }

            var bytes = await resp.Content.ReadAsByteArrayAsync(timeoutCts.Token).ConfigureAwait(false);
            return UpdateResourceResult.Ok(bytes);
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
        var result = await SigilDownloader
            .DownloadVerifiedAsync(url, destination, sha256, _timeout, _maxAttempts, _report, ct)
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
