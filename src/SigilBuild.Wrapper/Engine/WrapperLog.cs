using System;
using System.IO;
using System.Text;

namespace SigilBuild.Wrapper.Engine;

/// <summary>
/// Append-only file logger for the wrapper runtime. Writes timestamped lines
/// to a <c>sigil-logs/</c> subdirectory next to setup.exe and flushes after
/// every line so a partial log survives an abrupt termination (crash, kill,
/// power loss).
/// </summary>
/// <remarks>
/// <para>
/// Log directory selection: by preference, <c>{Path.GetDirectoryName(Environment.ProcessPath)}\sigil-logs</c>
/// so the log lives next to the running setup.exe — easy for the end-user to
/// find, easy to email back when debugging. If that directory can't be created
/// (read-only mount, network share without write access, USB stick with the
/// filesystem mounted RO, etc.) the logger falls back to <c>%TEMP%</c>. The
/// chosen directory is then exposed via <see cref="LogDirectory"/> so
/// <c>InstallerHostLauncher</c> can point the wizard at the same location.
/// </para>
/// <para>
/// Initialization is lazy + idempotent — the first <see cref="Info"/> call
/// allocates the log file.
/// </para>
/// <para>
/// Lock contention is not a concern: the wrapper is a single-threaded console
/// app and the launched wizard process writes to its own separate log file
/// (<c>InstallerLog</c>) — they happen to share the same directory but never
/// the same file.
/// </para>
/// <para>
/// All file-IO failures are swallowed. We deliberately do NOT throw from the
/// logger — a missing log is a worse experience than a degraded one, but
/// crashing the wrapper because we couldn't write a log line would be worst.
/// </para>
/// </remarks>
internal static class WrapperLog
{
    private const string LogSubdir = "sigil-logs";
    private const string SharedLogEnvVar = "SIGIL_LOG_FILE";
    private const string Role = "WRAPPER";

    private static readonly object _lock = new();
    private static string? _path;
    private static string? _logDir;
    private static bool _initFailed;

    /// <summary>
    /// Full path to the wrapper log file. <c>null</c> when initialization
    /// failed (in which case all <see cref="Info"/> / <see cref="Error"/>
    /// calls are no-ops).
    /// </summary>
    public static string? LogPath
    {
        get
        {
            EnsureInit();
            return _initFailed ? null : _path;
        }
    }

    /// <summary>
    /// Directory the wrapper chose for its log — either <c>{setupDir}\sigil-logs</c>
    /// or <c>%TEMP%</c>. The wizard launcher passes this directory to the
    /// wizard via <c>SIGIL_LOG_DIR</c> so both halves of the install land in
    /// the same place.
    /// </summary>
    public static string? LogDirectory
    {
        get
        {
            EnsureInit();
            return _initFailed ? null : _logDir;
        }
    }

    public static void Info(string message) => Append("INFO ", message);

    public static void Error(string message) => Append("ERROR", message);

    public static void Error(string message, Exception ex) =>
        Append("ERROR", $"{message}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    private static void EnsureInit()
    {
        if (_path is not null || _initFailed) return;
        lock (_lock)
        {
            if (_path is not null || _initFailed) return;
            try
            {
                // Honour an inherited SIGIL_LOG_FILE first — that's how the
                // parent wrapper hands a single shared log path down to the
                // wizard and (transitively) to the silent grandchild. The
                // first process in the chain (parent setup.exe via Explorer)
                // gets null here and creates a fresh file, then exports the
                // path via Environment.SetEnvironmentVariable for child PSI
                // inheritance.
                var fromEnv = System.Environment.GetEnvironmentVariable(SharedLogEnvVar);
                if (!string.IsNullOrWhiteSpace(fromEnv))
                {
                    _path = fromEnv;
                    _logDir = Path.GetDirectoryName(fromEnv) ?? Path.GetTempPath();
                }
                else
                {
                    var ts = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                    var pid = System.Environment.ProcessId;
                    // Per-host suffix mirrors the NSIS .onInit convention of
                    // tagging logs with the machine name so a tech can triage
                    // an installation directory full of failed-install logs
                    // without opening each file. MachineName is sanitised
                    // against filename-invalid characters defensively.
                    var host = SanitiseFilenameSegment(System.Environment.MachineName);
                    _logDir = ResolveLogDirectory();
                    _path = Path.Combine(_logDir, $"sigil-install-{ts}-{host}-{pid}.log");
                    // Export to env so wizard and grandchild inherit and append
                    // to the same file instead of generating their own.
                    System.Environment.SetEnvironmentVariable(SharedLogEnvVar, _path);
                }

                AppendLine($"# sigil-wrapper log {DateTime.UtcNow:O} pid={System.Environment.ProcessId} role={Role} setup={System.Environment.ProcessPath}");
            }
#pragma warning disable CA1031 // Logger swallows: see <remarks> on the type.
            catch
            {
                _initFailed = true;
                _path = null;
                _logDir = null;
            }
#pragma warning restore CA1031
        }
    }

    /// <summary>
    /// Replace any character invalid in a filename with underscore. Defensive
    /// against an oddly-named hostname (some VM provisioning produces dots /
    /// spaces / Cyrillic in <see cref="System.Environment.MachineName"/>).
    /// </summary>
    private static string SanitiseFilenameSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment)) return "host";
        var invalid = Path.GetInvalidFileNameChars();
        Span<char> buf = stackalloc char[segment.Length];
        var i = 0;
        foreach (var c in segment)
        {
            buf[i++] = (c is '/' or '\\' or ':' || Array.IndexOf(invalid, c) >= 0) ? '_' : c;
        }
        return new string(buf[..i]);
    }

    /// <summary>
    /// Prefer a <c>sigil-logs/</c> subdir next to setup.exe. Fall back to
    /// <c>%TEMP%</c> when the preferred directory can't be created (USB stick
    /// mounted read-only, network share without write, missing/odd
    /// <see cref="System.Environment.ProcessPath"/>, etc.).
    /// </summary>
    private static string ResolveLogDirectory()
    {
        var setupPath = System.Environment.ProcessPath;
        if (!string.IsNullOrEmpty(setupPath))
        {
            var setupDir = Path.GetDirectoryName(setupPath);
            if (!string.IsNullOrEmpty(setupDir))
            {
                var preferred = Path.Combine(setupDir, LogSubdir);
                try
                {
                    Directory.CreateDirectory(preferred);
                    // Write-probe — confirm we can actually write here before
                    // committing the path. Failure → fall through to %TEMP%.
                    var probe = Path.Combine(preferred, $".write-probe-{Guid.NewGuid():N}");
                    File.WriteAllText(probe, "");
                    File.Delete(probe);
                    return preferred;
                }
#pragma warning disable CA1031
                catch
                {
                    // Read-only mount or no perms — fall through.
                }
#pragma warning restore CA1031
            }
        }
        return Path.GetTempPath();
    }

    private static void Append(string level, string message)
    {
        EnsureInit();
        if (_initFailed || _path is null) return;
        lock (_lock)
        {
            try
            {
                var line = $"{DateTime.UtcNow:HH:mm:ss.fff} [{Role}] {level} {message}\n";
                AppendLine(line);
            }
#pragma warning disable CA1031
            catch
            {
                // Same policy as init: swallow.
            }
#pragma warning restore CA1031
        }
    }

    /// <summary>
    /// Concurrent-safe append. Multiple processes (parent wrapper, wizard,
    /// silent grandchild) share the same log file via SIGIL_LOG_FILE; we open
    /// with FileShare.ReadWrite so concurrent writes don't trip
    /// IOException("file is being used by another process") that
    /// <see cref="File.AppendAllText"/> raises by default.
    /// </summary>
    private static void AppendLine(string line)
    {
        if (_path is null) return;
        if (!line.EndsWith('\n')) line += "\n";
        using var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        var bytes = Encoding.UTF8.GetBytes(line);
        fs.Write(bytes, 0, bytes.Length);
    }
}
