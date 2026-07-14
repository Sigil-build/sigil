using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace SigilBuild.Wrapper.Engine;

/// <summary>
/// The single, AOT-safe install-log sink (P7, gap G8). Writes timestamped,
/// secret-redacted lines to the user-requested <c>/LOG</c> file for both the
/// headless (<c>/silent</c>) and wizard install paths, and for uninstall. One
/// level, no rotation, no verbosity switches (v1).
/// </summary>
/// <remarks>
/// <para>
/// Each line is appended with <see cref="FileShare.ReadWrite"/> and the stream is
/// closed immediately, so a crash mid-install never loses buffered lines and a
/// concurrent writer (e.g. an elevated child relaunched with the same
/// <c>/LOG=path</c>) does not fault on a locked file. This mirrors the wizard's
/// existing diagnostic logger.
/// </para>
/// <para>
/// All file-IO failures are swallowed: a missing or unwritable log must never
/// fail an install. <see cref="TryOpen"/> returns <c>null</c> when the target
/// cannot be created, and the caller then simply runs without a log.
/// </para>
/// </remarks>
public sealed class InstallLog
{
    private readonly string _path;
    private readonly object _gate = new();
    private IReadOnlyList<string> _secrets = Array.Empty<string>();

    private InstallLog(string path) => _path = path;

    /// <summary>The resolved absolute path this log writes to.</summary>
    public string Path => _path;

    /// <summary>
    /// Open (create/append) a log at <paramref name="path"/>, creating the parent
    /// directory if needed. Returns <c>null</c> — never throws — when the path is
    /// blank or cannot be created, so logging stays best-effort.
    /// </summary>
    public static InstallLog? TryOpen(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

#pragma warning disable CA1031 // Logging is best-effort: any open failure means "run without a log".
        try
        {
            var full = System.IO.Path.GetFullPath(path);
            var dir = System.IO.Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // Touch the file so it exists even for a run that emits no lines, and to
            // surface an unwritable path now (returns null) rather than mid-install.
            using (new FileStream(full, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
            }

            return new InstallLog(full);
        }
        catch
        {
            return null;
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Register the run's secret values so every subsequent line is redacted
    /// before it is written (ADR-008 §3 / decision 6). Set once, after the
    /// <see cref="StepContext"/> is built and before the engine runs.
    /// </summary>
    public void SetSecrets(IReadOnlyList<string> secrets)
    {
        lock (_gate)
        {
            _secrets = secrets ?? Array.Empty<string>();
        }
    }

    /// <summary>
    /// Append one timestamped, redacted line (<c>[UTC-ISO8601] message</c>).
    /// Best-effort: IO failures are swallowed so a write error never aborts the
    /// install.
    /// </summary>
    public void WriteLine(string message)
    {
        var stamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        lock (_gate)
        {
            var line = "[" + stamp + "] " + Redact(message ?? string.Empty);
#pragma warning disable CA1031 // Best-effort: a failed log write must not fail the install.
            try
            {
                if (!line.EndsWith('\n'))
                {
                    line += "\n";
                }
                using var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                var bytes = Encoding.UTF8.GetBytes(line);
                fs.Write(bytes, 0, bytes.Length);
            }
            catch
            {
                // swallow — see <remarks>.
            }
#pragma warning restore CA1031
        }
    }

    // Same contract as StepContext.Redact: replace every occurrence of a secret
    // value with ***. Defense-in-depth — engine progress lines are already
    // redacted, but the sink redacts again so no line can leak regardless of source.
    private string Redact(string text)
    {
        if (text.Length == 0 || _secrets.Count == 0)
        {
            return text;
        }

        var result = text;
        foreach (var secret in _secrets)
        {
            if (!string.IsNullOrEmpty(secret))
            {
                result = result.Replace(secret, "***", StringComparison.Ordinal);
            }
        }
        return result;
    }
}

/// <summary>
/// An <see cref="IProgress{T}"/> decorator (P7) that forwards each
/// <see cref="StepProgress"/> to an inner progress sink (console / wizard) and
/// also writes its message to the <see cref="InstallLog"/>. This is how the one
/// engine run produces identical step / rollback lines on the console, in the
/// wizard's log pane, and in the <c>/LOG</c> file.
/// </summary>
public sealed class LoggingProgress : IProgress<StepProgress>
{
    private readonly IProgress<StepProgress>? _inner;
    private readonly InstallLog _log;

    public LoggingProgress(IProgress<StepProgress>? inner, InstallLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _inner = inner;
        _log = log;
    }

    public void Report(StepProgress value)
    {
        _inner?.Report(value);
        if (value.Message is not null)
        {
            _log.WriteLine(value.Message);
        }
    }
}
