using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace SigilBuild.Wrapper.Engine;

/// <summary>
/// Wrapper-runtime half of the Phase 2.5 ↔ 2.6 integration. Reads the optional
/// <c>SIGIL_INSTALLER_HOST_V1</c> resource embedded by <c>ExeWrapperPackager</c>,
/// extracts <c>sigil-wizard.exe</c> + <c>BrandTokens.g.json</c> to a per-session
/// temp directory, launches the Avalonia wizard, and waits for it to exit.
/// </summary>
/// <remarks>
/// <para>
/// Wizard exit-code convention (mirrors MSI; see
/// <see cref="SigilBuild.Installer.Host.ViewModels.InstallerOutcomeCode"/>):
/// </para>
/// <list type="bullet">
///   <item><description><c>0</c> — user clicked through to Finish; proceed with install_steps.</description></item>
///   <item><description><c>1602</c> — user cancelled; the wrapper exits with the same code and does NOT run install_steps.</description></item>
///   <item><description><c>1603</c> — wizard reported a generic failure; treated the same as cancel for the wrapper's purposes.</description></item>
/// </list>
/// <para>
/// The temp directory is best-effort cleaned up on dispose. Failures to clean
/// up are swallowed because Windows occasionally locks the launched
/// installer.exe for a brief window after the process exits.
/// </para>
/// </remarks>
internal sealed class InstallerHostLauncher : IDisposable
{
    /// <summary>
    /// Must match <c>InstallerHostBundle.WizardEntryName</c> on the pack side.
    /// Renaming the extracted wizard away from "installer.exe" / "setup.exe"
    /// dodges Windows Installer Detection's auto-UAC heuristic — the wizard
    /// inherits the parent setup.exe's elevation token via normal CreateProcess
    /// instead of triggering a second UAC prompt that <c>UseShellExecute=false</c>
    /// can't handle (Win32 error 740).
    /// </summary>
    private const string WizardFileName = "sigil-wizard.exe";

    private readonly string _tempRoot;
    private bool _disposed;

    private InstallerHostLauncher(string tempRoot)
    {
        _tempRoot = tempRoot;
    }

    /// <summary>
    /// Look for the <c>SIGIL_INSTALLER_HOST_V1</c> resource in the running
    /// executable. Returns <c>null</c> when the resource isn't embedded — the
    /// expected path for manifests without an <c>installer:</c> block, or when
    /// the SDK didn't have an installer.exe staged at pack time. Callers fall
    /// through to the headless install_steps path.
    /// </summary>
    public static InstallerHostLauncher? TryPrepare()
    {
        var bundleBytes = WrapperBlob.LoadInstallerHostBundleBytes();
        if (bundleBytes is null || bundleBytes.Length == 0)
        {
            WrapperLog.Info("InstallerHostLauncher.TryPrepare: no SIGIL_INSTALLER_HOST_V1 resource embedded — returning null");
            return null;
        }

        WrapperLog.Info($"InstallerHostLauncher.TryPrepare: bundle size {bundleBytes.Length:N0} bytes");

        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            "sigil-installer-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        WrapperLog.Info($"InstallerHostLauncher.TryPrepare: extracting to {tempRoot}");

        try
        {
            using var ms = new MemoryStream(bundleBytes);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
            foreach (var entry in zip.Entries)
            {
                // Defensive: refuse any entry whose decompressed path would
                // escape the temp root (zip-slip).
                var dest = Path.GetFullPath(Path.Combine(tempRoot, entry.FullName));
                if (!dest.StartsWith(tempRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"installer host bundle contains a zip-slip entry: '{entry.FullName}'");
                }
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: true);
                WrapperLog.Info($"  extracted: {entry.FullName} ({entry.Length:N0} bytes)");
            }
            return new InstallerHostLauncher(tempRoot);
        }
        catch (Exception ex)
        {
            WrapperLog.Error("InstallerHostLauncher.TryPrepare: extract failed", ex);
            // Failed mid-extract — leave nothing partial behind for the launcher.
            try { Directory.Delete(tempRoot, recursive: true); } catch { /* best-effort */ }
            throw;
        }
    }

    /// <summary>
    /// Launch installer.exe and wait for it to exit. Returns the wizard's
    /// process exit code so the caller (<c>Program.Main</c>) can short-circuit
    /// install_steps on user cancel.
    /// </summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var hostExe = Path.Combine(_tempRoot, WizardFileName);
        if (!File.Exists(hostExe))
        {
            throw new InvalidOperationException(
                $"installer host bundle was extracted but {WizardFileName} is missing under {_tempRoot}");
        }

        // SIGIL_LOG_FILE was already set on this process by WrapperLog.EnsureInit;
        // the wizard inherits it via Process.Start and writes to the same file
        // through InstallerLog. So the wrapper + wizard + silent grandchild all
        // append to one log per install session instead of three.
        var sharedLogPath = WrapperLog.LogPath ?? "<no log>";

        var psi = new ProcessStartInfo
        {
            FileName = hostExe,
            WorkingDirectory = _tempRoot,
            UseShellExecute = false,
            CreateNoWindow = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // The wizard's Installing screen re-launches setup.exe with /S to run
        // install_steps as a child — that's how UAC-inherited elevation reaches
        // sc.exe / HKLM writes / etc. without a second prompt. The wrapper IS
        // setup.exe (Environment.ProcessPath); pass that path along so the
        // wizard doesn't have to guess it from process-tree heuristics.
        var setupExePath = System.Environment.ProcessPath;
        if (!string.IsNullOrEmpty(setupExePath))
        {
            psi.EnvironmentVariables["SIGIL_SETUP_EXE"] = setupExePath;
        }

        WrapperLog.Info($"InstallerHostLauncher.RunAsync: launching '{hostExe}' (shared log → {sharedLogPath})");

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Process.Start returned null for the wizard host");

        // Pipe child stdout/stderr into the wrapper log so any unhandled
        // Avalonia init exception lands somewhere visible. We drain
        // asynchronously to avoid the classic 4KB pipe-buffer deadlock.
        var stdoutTask = DrainAsync(proc.StandardOutput, "wizard stdout: ", ct);
        var stderrTask = DrainAsync(proc.StandardError, "wizard stderr: ", ct);

        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

        WrapperLog.Info($"InstallerHostLauncher.RunAsync: wizard exited with code {proc.ExitCode}");
        return proc.ExitCode;
    }

    private static async Task DrainAsync(System.IO.StreamReader reader, string prefix, CancellationToken ct)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
            {
                WrapperLog.Info(prefix + line);
            }
        }
        catch (Exception ex)
        {
            WrapperLog.Error($"drain failed for '{prefix}'", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Best-effort cleanup — the OS will mop up %TEMP% on reboot if a stray
        // file handle keeps anything alive. We deliberately keep the wizard
        // log file (which lives outside _tempRoot) so the user can read it
        // after the wrapper exits.
#pragma warning disable CA1031
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
                WrapperLog.Info($"InstallerHostLauncher.Dispose: cleaned up {_tempRoot}");
            }
        }
        catch (Exception ex)
        {
            WrapperLog.Error($"InstallerHostLauncher.Dispose: cleanup of {_tempRoot} failed", ex);
        }
#pragma warning restore CA1031
    }
}
