using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SigilBuild.Installer.Host.Services;

public sealed record InstallProgress(double FractionDone, string CurrentStep);

public sealed class InstallerEngine
{
    private readonly string _payloadDirectory;
    private readonly string _targetDirectory;

    public InstallerEngine(string payloadDirectory, string targetDirectory)
    {
        _payloadDirectory = payloadDirectory;
        _targetDirectory = targetDirectory;
    }

    public async Task RunAsync(IProgress<InstallProgress> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(_targetDirectory);
        var files = Directory.GetFiles(_payloadDirectory, "*", SearchOption.AllDirectories);
        for (var i = 0; i < files.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(_payloadDirectory, files[i]);
            var dst = Path.Combine(_targetDirectory, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(files[i], dst, overwrite: true);
            progress.Report(new InstallProgress((i + 1.0) / files.Length, rel));
            await Task.Yield();
        }
    }
}
