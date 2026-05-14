using System;
using System.IO;
using System.Text;

namespace SigilBuild.Installer.Host;

/// <summary>
/// Append-only file logger for the wizard. The wizard runs as a Windows GUI
/// process (WinExe) — there is no console, so unhandled exceptions during
/// Avalonia init disappear unless we write them to a file. Pairs with
/// <c>SigilBuild.Wrapper.Engine.WrapperLog</c>: the wrapper passes a target
/// path via the <c>SIGIL_WIZARD_LOG</c> environment variable so the two logs
/// can be correlated by timestamp.
/// </summary>
/// <remarks>
/// <para>
/// Fallback path when <c>SIGIL_WIZARD_LOG</c> isn't set:
/// <c>%TEMP%\sigil-wizard-{yyyyMMdd-HHmmss}-{pid}.log</c>.
/// </para>
/// <para>
/// All file-IO failures are swallowed — a missing log is acceptable; crashing
/// the wizard because we couldn't write a log line is not.
/// </para>
/// </remarks>
internal static class InstallerLog
{
    private const string SharedLogEnvVar = "SIGIL_LOG_FILE";
    private const string LegacyWizardLogEnvVar = "SIGIL_WIZARD_LOG";
    private const string Role = "WIZARD";

    private static readonly object _lock = new();
    private static string? _path;
    private static bool _initFailed;

    public static string? LogPath
    {
        get
        {
            EnsureInit();
            return _initFailed ? null : _path;
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
                // Honour the parent wrapper's SIGIL_LOG_FILE first — that's
                // how parent + wizard + grandchild end up writing to the SAME
                // file (one log per install session instead of three).
                // SIGIL_WIZARD_LOG is kept as a back-compat fallback for older
                // setup.exe builds that haven't been re-packed yet.
                var fromEnv = Environment.GetEnvironmentVariable(SharedLogEnvVar)
                              ?? Environment.GetEnvironmentVariable(LegacyWizardLogEnvVar);
                if (!string.IsNullOrWhiteSpace(fromEnv))
                {
                    _path = fromEnv;
                }
                else
                {
                    var ts = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                    var pid = Environment.ProcessId;
                    _path = Path.Combine(Path.GetTempPath(), $"sigil-wizard-{ts}-{pid}.log");
                }

                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                AppendLine($"# sigil-wizard log {DateTime.UtcNow:O} pid={Environment.ProcessId} role={Role} exe={Environment.ProcessPath}");
            }
#pragma warning disable CA1031 // Logger swallows — see <remarks> on the type.
            catch
            {
                _initFailed = true;
                _path = null;
            }
#pragma warning restore CA1031
        }
    }

    private static void Append(string level, string message)
    {
        EnsureInit();
        if (_initFailed || _path is null) return;
        lock (_lock)
        {
            try
            {
                var line = $"{DateTime.UtcNow:HH:mm:ss.fff} [{Role}] {level} {message}";
                AppendLine(line);
            }
#pragma warning disable CA1031
            catch
            {
            }
#pragma warning restore CA1031
        }
    }

    /// <summary>
    /// Concurrent-safe append. The shared log is written by the wrapper
    /// (parent + grandchild) and the wizard at the same time;
    /// <see cref="File.AppendAllText"/>'s default FileShare.Read blocks
    /// concurrent processes, so use a FileStream with ReadWrite share mode.
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
