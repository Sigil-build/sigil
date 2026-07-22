namespace SigilBuild.Wrapper.Engine;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Core.Localization;
using SigilBuild.Wrapper.Expressions;

/// <summary>
/// Outcome of the prerequisite phase (P5, gap G6). <see cref="Success"/> is false
/// when a prerequisite could not be satisfied — the caller aborts BEFORE the
/// rollback journal opens, so no partial install results. <see cref="RebootRequired"/>
/// is set when any prerequisite installer returned exit code 3010.
/// </summary>
public readonly record struct PrerequisiteOutcome(bool Success, string? Error, bool RebootRequired)
{
    public static PrerequisiteOutcome Ok(bool rebootRequired) => new(true, null, rebootRequired);
    public static PrerequisiteOutcome Failed(string error) => new(false, error, false);
}

/// <summary>
/// Runs <c>installer.prerequisites[]</c> (P5, gap G6) sequentially, BEFORE the
/// transactional install body and the P2 <c>pre_install</c> hooks (and before the
/// rollback journal opens) — the declarative equivalent of Burn's ExePackage +
/// DetectCondition. Per prerequisite: evaluate <c>detect</c> (skip when already
/// satisfied), else acquire the source (a bundled <c>payload://</c> file or a
/// verified <c>https://</c> download via <see cref="SigilDownloader"/>), run it with
/// its args, accept an exit code in <c>exit_codes_ok</c> (default <c>[0]</c>), then
/// re-evaluate <c>detect</c> and fail if it is still false. An accepted exit code of
/// 3010 flags reboot-required. <c>scope_required</c> mismatches are diagnosed up front.
/// </summary>
/// <remarks>
/// Prerequisites are NEVER journaled — a shared machine dependency (VC++ redist,
/// .NET runtime) is not rolled back. Progress rows are message-only
/// (<c>Total = 0</c>) so they log and show without moving the wizard's progress bar.
/// </remarks>
public static class PrerequisiteRunner
{
    private const int RebootExitCode = 3010;
    private const int DefaultTimeoutSeconds = 600;
    private const int DownloadAttempts = 3;

    /// <summary>
    /// Test seam: launch a prerequisite installer and return its exit code (or a start
    /// error). Defaults to the real <see cref="Process"/> launcher; unit tests inject a
    /// fake so the decision logic is exercised without spawning a process.
    /// </summary>
    internal delegate Task<(int ExitCode, string? Error)> Launcher(
        string exePath, IReadOnlyList<string> args, int? timeoutSeconds, CancellationToken ct);

    public static Task<PrerequisiteOutcome> RunAsync(
        IReadOnlyList<InstallerPrerequisite>? prerequisites,
        StepContext ctx,
        InstallScope scope,
        IProgress<StepProgress>? progress,
        CancellationToken ct)
        => RunAsync(prerequisites, ctx, scope, progress, launcher: null, ct);

    internal static async Task<PrerequisiteOutcome> RunAsync(
        IReadOnlyList<InstallerPrerequisite>? prerequisites,
        StepContext ctx,
        InstallScope scope,
        IProgress<StepProgress>? progress,
        Launcher? launcher,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (prerequisites is null || prerequisites.Count == 0)
        {
            return PrerequisiteOutcome.Ok(false);
        }

        launcher ??= LaunchProcessAsync;

        // 1. Scope-required check for ALL prerequisites up front — a diagnostic at
        //    session start, before anything is downloaded or run.
        foreach (var p in prerequisites)
        {
            var mismatch = ScopeMismatch(p.ScopeRequired, scope);
            if (mismatch is not null)
            {
                return PrerequisiteOutcome.Failed($"prerequisite '{p.Name}' {mismatch}");
            }
        }

        var rebootRequired = false;
        foreach (var p in prerequisites)
        {
            ct.ThrowIfCancellationRequested();

            // a. detect → skip if already satisfied. A malformed detect expression is a
            //    hard failure with a clear message (never a silent "not satisfied").
            var (satisfied, detectError) = TryEvaluateDetect(ctx, p.Detect);
            if (detectError is not null)
            {
                return PrerequisiteOutcome.Failed($"prerequisite '{p.Name}' detect expression could not be evaluated: {detectError}");
            }
            if (satisfied)
            {
                Report(progress, ctx, $"prerequisite: {p.Name} already present — skipped", isError: false);
                continue;
            }

            Report(progress, ctx, Strings.EngineInstallingPrerequisite(SessionLanguage.Current, p.Name), isError: false);

            // b. acquire the source (bundled payload or verified download).
            var (exePath, tempPath, acquireError) = await AcquireAsync(p, ctx, progress, ct).ConfigureAwait(false);
            if (acquireError is not null)
            {
                return PrerequisiteOutcome.Failed($"prerequisite '{p.Name}': {acquireError}");
            }

            int exitCode;
            try
            {
                // c. run it.
                var args = ResolveArgs(p.Args, ctx);
                var (code, runError) = await launcher(exePath!, args, p.TimeoutSeconds, ct).ConfigureAwait(false);
                if (runError is not null)
                {
                    return PrerequisiteOutcome.Failed($"prerequisite '{p.Name}': {runError}");
                }
                exitCode = code;
            }
            finally
            {
                if (tempPath is not null)
                {
                    TryDelete(tempPath); // prereqs are not journaled; clean the temp download.
                }
            }

            // d. accept the exit code. 3010 is the universal Windows "success, reboot
            //    required" code — always accepted (like WiX Burn / MSI) and flags reboot,
            //    even if the author did not list it in exit_codes_ok. Otherwise the code
            //    must be in exit_codes_ok (default [0]).
            var okCodes = p.ExitCodesOk is { Count: > 0 } ? p.ExitCodesOk : DefaultOk;
            var isReboot = exitCode == RebootExitCode;
            if (!isReboot && !Contains(okCodes, exitCode))
            {
                return PrerequisiteOutcome.Failed(
                    $"prerequisite '{p.Name}' installer exited {exitCode}; expected one of [{string.Join(", ", okCodes)}]");
            }
            if (isReboot)
            {
                // A reboot-pending component is not yet active (its registration lands on
                // the next boot), so the re-detect guard CANNOT verify it in-process —
                // skip the guard and flag the session reboot-required.
                rebootRequired = true;
                Report(progress, ctx, $"prerequisite: {p.Name} installed — reboot required before it is active", isError: false);
                continue;
            }

            // e. detect-after-install guard: re-evaluate detect and fail if still false.
            var (nowSatisfied, redetectError) = TryEvaluateDetect(ctx, p.Detect);
            if (redetectError is not null)
            {
                return PrerequisiteOutcome.Failed($"prerequisite '{p.Name}' detect expression could not be evaluated: {redetectError}");
            }
            if (!nowSatisfied)
            {
                return PrerequisiteOutcome.Failed(
                    $"prerequisite '{p.Name}' ran (exit {exitCode}) but is still not detected — the installation did not take effect");
            }

            Report(progress, ctx, $"prerequisite: {p.Name} installed", isError: false);
        }

        return PrerequisiteOutcome.Ok(rebootRequired);
    }

    private static readonly IReadOnlyList<int> DefaultOk = new[] { 0 };

    // Evaluate a `when`-grammar boolean detect expression. Returns (satisfied, null)
    // normally, or (false, message) when the expression is malformed / references an
    // unknown function or identifier — the caller turns that into a clear prerequisite
    // failure rather than silently treating it as "not satisfied".
    private static (bool Satisfied, string? Error) TryEvaluateDetect(StepContext ctx, string detect)
    {
        try
        {
            return (ctx.Evaluate(detect), null);
        }
        catch (ExpressionException ex)
        {
            return (false, ex.Message);
        }
    }

    private static string? ScopeMismatch(string? scopeRequired, InstallScope scope) => scopeRequired switch
    {
        null or "" => null,
        "allusers" when scope != InstallScope.Machine =>
            "requires an all-users (per-machine) install, but this run is per-user — re-run with /allusers (elevated)",
        "currentuser" when scope != InstallScope.User =>
            "requires a current-user (per-user) install, but this run is per-machine — re-run with /currentuser",
        _ => null,
    };

    private static async Task<(string? ExePath, string? TempPath, string? Error)> AcquireAsync(
        InstallerPrerequisite p, StepContext ctx, IProgress<StepProgress>? progress, CancellationToken ct)
    {
        var source = p.Source;

        // Bundled: resolve the payload:// path to the extracted file.
        if (source.StartsWith("payload://", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var path = ctx.ResolvePath(source);
                return File.Exists(path)
                    ? (path, null, null)
                    : (null, null, $"bundled source not found: {source}");
            }
            catch (FormatException ex)
            {
                return (null, null, ex.Message);
            }
        }

        // Downloaded: resolve {var.*} tokens, enforce https + sha256, verify to a temp file.
        var url = ctx.Resolve(source);
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return (null, null, $"source must be a payload:// or https:// URL (got '{url}')");
        }
        var sha = (p.Sha256 ?? string.Empty).Trim();
        if (sha.Length == 0)
        {
            return (null, null, "an https:// source requires a sha256 checksum");
        }

        var temp = Path.Combine(Path.GetTempPath(), $"sigil-prereq-{Guid.NewGuid():N}.exe");
        var timeout = TimeSpan.FromSeconds(p.TimeoutSeconds is int t and > 0 ? t : DefaultTimeoutSeconds);
        var result = await SigilDownloader.DownloadVerifiedAsync(
            url, temp, sha, timeout, DownloadAttempts,
            report: (msg, isErr) => Report(progress, ctx, msg, isErr),
            ct).ConfigureAwait(false);

        if (!result.Success)
        {
            TryDelete(temp);
            return (null, null, result.Error);
        }
        return (temp, temp, null);
    }

    // Real process launcher: mirrors run_program (redirected pipes drained, timeout
    // via a linked CTS, exit code returned). No journal entry — prereqs are not undone.
    private static async Task<(int ExitCode, string? Error)> LaunchProcessAsync(
        string exePath, IReadOnlyList<string> args, int? timeoutSeconds, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        Process? proc;
        try
        {
            proc = Process.Start(psi);
        }
#pragma warning disable CA1031 // Surface any spawn failure as a typed error.
        catch (Exception ex)
        {
            return (-1, $"failed to start '{exePath}': {ex.Message}");
        }
#pragma warning restore CA1031
        if (proc is null)
        {
            return (-1, $"could not start '{exePath}'");
        }

        using (proc)
        {
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);

            using var timeoutCts = timeoutSeconds is int ts and > 0
                ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                : null;
            timeoutCts?.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds!.Value));
            var waitToken = timeoutCts?.Token ?? ct;

            try
            {
                await proc.WaitForExitAsync(waitToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
#pragma warning disable CA1031 // Best-effort kill — the process may already have died.
                try { proc.Kill(entireProcessTree: true); }
                catch { /* best-effort */ }
#pragma warning restore CA1031
                return (-1, $"installer timed out after {timeoutSeconds}s");
            }

            // Drain so the child's pipe buffer never blocks its exit.
            await stdoutTask.ConfigureAwait(false);
            await stderrTask.ConfigureAwait(false);
            return (proc.ExitCode, null);
        }
    }

    private static string[] ResolveArgs(IReadOnlyList<string>? args, StepContext ctx)
    {
        if (args is null || args.Count == 0)
        {
            return Array.Empty<string>();
        }
        var result = new string[args.Count];
        for (var i = 0; i < args.Count; i++)
        {
            result[i] = ctx.Resolve(args[i]);
        }
        return result;
    }

    private static bool Contains(IReadOnlyList<int> codes, int code)
    {
        for (var i = 0; i < codes.Count; i++)
        {
            if (codes[i] == code) return true;
        }
        return false;
    }

    private static void TryDelete(string path)
    {
#pragma warning disable CA1031 // Best-effort cleanup of a temp download.
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort */ }
#pragma warning restore CA1031
    }

    private static void Report(IProgress<StepProgress>? progress, StepContext ctx, string message, bool isError)
        => progress?.Report(new StepProgress(0, 0, ctx.Redact(message), isError));
}
