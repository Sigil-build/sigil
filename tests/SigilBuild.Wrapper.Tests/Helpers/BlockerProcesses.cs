using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;

namespace SigilBuild.Wrapper.Tests.Helpers;

/// <summary>
/// R58 — a real, separate process that holds a data file open under a directory the
/// files-in-use sweep will scan. The Restart Manager's positive control: after the R58
/// self-exclusion, a blocker has to be somebody OTHER than the running installer for an
/// assertion about it to mean anything.
/// </summary>
/// <remarks>
/// <c>powershell.exe</c> is the holder because its own image lives in System32, far
/// outside the scanned directory, so the only thing that can tie it to the sweep is the
/// open handle — which is what the control is meant to prove. It opens the file with
/// <c>FileShare.None</c>, touches a ready flag OUTSIDE the scanned directory (a flag
/// inside it would add a file to the very sweep under test), then sleeps until killed.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class FileHolderProcess : IDisposable
{
    private readonly Process _process;

    private FileHolderProcess(Process process) => _process = process;

    /// <summary>The holder's process id — what the sweep is expected to report.</summary>
    public int Id => _process.Id;

    /// <summary>
    /// Start a holder on <paramref name="filePath"/> and block until it reports the
    /// handle open by creating <paramref name="readyFlagPath"/> (which must not live
    /// inside the directory being swept).
    /// </summary>
    public static FileHolderProcess Start(string filePath, string readyFlagPath)
    {
        var script =
            $"$f = [System.IO.File]::Open('{filePath}', 'Open', 'ReadWrite', 'None'); " +
            $"[System.IO.File]::WriteAllText('{readyFlagPath}', 'ready'); " +
            "Start-Sleep -Seconds 300";

        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Never the scanned directory: a cwd there would itself be a handle.
            WorkingDirectory = Path.GetTempPath(),
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("could not start the file-holder process");

        var holder = new FileHolderProcess(process);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (!File.Exists(readyFlagPath))
        {
            if (process.HasExited || DateTime.UtcNow > deadline)
            {
                holder.Kill();
                throw new InvalidOperationException(
                    $"the file-holder process never opened '{filePath}' " +
                    $"(exited: {process.HasExited})");
            }
            Thread.Sleep(100);
        }
        return holder;
    }

    /// <summary>Stop the holder, releasing the handle. Safe to call more than once.</summary>
    public void Kill()
    {
#pragma warning disable CA1031 // Test cleanup: a dead holder is the desired end state.
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(10_000);
            }
        }
        catch (Exception)
        {
            // best-effort
        }
#pragma warning restore CA1031
    }

    public void Dispose()
    {
        Kill();
        _process.Dispose();
    }
}

/// <summary>
/// R58 — a real, separate process whose EXECUTABLE IMAGE lives inside the directory the
/// sweep scans, holding no other handle there. Two things rest on it: the Restart Manager
/// reports such a process (so "the app you are upgrading is running" keeps being caught),
/// and it is the exact shape of T12's un-elevated relaunch parent, which is why the R58
/// exclusion is keyed on the image path.
/// </summary>
/// <remarks>
/// The image is a copy of <c>ping.exe</c>: a tiny, always-present System32 executable
/// whose imports all resolve from the system path, so it runs happily from a temp
/// directory, and <c>-n</c> makes it sit there until killed without needing a console.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class ImageInDirProcess : IDisposable
{
    private readonly Process _process;

    private ImageInDirProcess(Process process, string imagePath)
    {
        _process = process;
        ImagePath = imagePath;
    }

    /// <summary>The process id — what the sweep is expected to report.</summary>
    public int Id => _process.Id;

    /// <summary>The full path of the image inside the scanned directory.</summary>
    public string ImagePath { get; }

    /// <summary>
    /// Copy the stand-in image into <paramref name="directory"/> under
    /// <paramref name="exeName"/> and run it. Blocks only until the process object
    /// exists; callers poll the sweep, because the Restart Manager sees the image a
    /// moment after <c>CreateProcess</c> returns.
    /// </summary>
    public static ImageInDirProcess Start(string directory, string exeName)
    {
        Directory.CreateDirectory(directory);
        var imagePath = Path.GetFullPath(Path.Combine(directory, exeName));
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), imagePath, overwrite: true);

        var psi = new ProcessStartInfo(imagePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Deliberately NOT the scanned directory — the point of this holder is that
            // its image is the only thing tying it to the sweep.
            WorkingDirectory = Path.GetTempPath(),
        };
        psi.ArgumentList.Add("-n");
        psi.ArgumentList.Add("300");
        psi.ArgumentList.Add("127.0.0.1");

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"could not start '{imagePath}'");
        return new ImageInDirProcess(process, imagePath);
    }

    /// <summary>Stop the process so the directory can be deleted. Safe to call twice.</summary>
    public void Kill()
    {
#pragma warning disable CA1031 // Test cleanup: a dead holder is the desired end state.
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(10_000);
            }
        }
        catch (Exception)
        {
            // best-effort
        }
#pragma warning restore CA1031
    }

    public void Dispose()
    {
        Kill();
        _process.Dispose();
    }
}
