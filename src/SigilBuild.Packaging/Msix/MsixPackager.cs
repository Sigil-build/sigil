using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SigilBuild.Core.Diagnostics;
using SigilBuild.Core.Manifest;
using SigilBuild.Packaging.Common;
using SigilBuild.Packaging.Installer;

namespace SigilBuild.Packaging.Msix;

public sealed class MsixPackager : IPackager
{
    public PackageFormat Format => PackageFormat.Msix;

    public async Task<PackResult> PackAsync(SigilManifest manifest, PackOptions options, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new PackResult(null, new[]
            {
                new Diagnostic(DiagnosticSeverity.Error, "SIG0100",
                    "MSIX packaging requires Windows (D-004 MVP scope)",
                    SourceLocation.Unknown,
                    "https://docs.sigil.build/diagnostics/SIG0100"),
            });
        }

        if (!WindowsSdkLocator.TryLocateBin(out var sdkBin))
        {
            return new PackResult(null, new[]
            {
                new Diagnostic(DiagnosticSeverity.Error, "SIG0101",
                    "Windows 10/11 SDK not found; install from https://aka.ms/winsdk",
                    SourceLocation.Unknown,
                    "https://docs.sigil.build/diagnostics/SIG0101"),
            });
        }

        var stagingDir = Path.Combine(Path.GetTempPath(), "sigil-msix-" + Path.GetRandomFileName());
        Directory.CreateDirectory(stagingDir);
        try
        {
            CopyTree(options.SourceDirectory, stagingDir, manifest.Build.Include, manifest.Build.Exclude);

            var manifestXml = AppxManifestBuilder.Build(manifest, options.Architecture);
            await File.WriteAllTextAsync(Path.Combine(stagingDir, "AppxManifest.xml"), manifestXml, ct);

            var assetsDir = Path.Combine(stagingDir, "Assets");
            Directory.CreateDirectory(assetsDir);
            var logo = manifest.Package?.Msix?.Logo;
            if (!string.IsNullOrEmpty(logo) && File.Exists(logo))
                LogoAssetGenerator.Generate(logo, assetsDir);
            else
                CreatePlaceholderAssets(assetsDir);

            if (manifest.Installer is not null)
            {
                var envExe = Environment.GetEnvironmentVariable("SIGIL_INSTALLER_HOST_EXE");
                var hostExe = envExe ?? Path.Combine(AppContext.BaseDirectory, "installer", "installer.exe");
                // When env var is explicitly set, treat it as authoritative — Bundle will throw if missing.
                // On the fallback path, skip silently if the host binary hasn't been published yet.
                if (envExe is not null || File.Exists(hostExe))
                    InstallerHostBundler.Bundle(manifest, hostExe, stagingDir);
            }

            Directory.CreateDirectory(options.OutputDirectory);
            var archStr = options.Architecture.ToString().ToLowerInvariant();
            var fileName = $"{manifest.App.Id}-{manifest.App.Version}-{archStr}.msix";
            var outPath = Path.Combine(options.OutputDirectory, fileName);

            var runner = new MakeAppxRunner(Path.Combine(sdkBin, "MakeAppx.exe"));
            var run = await runner.PackAsync(stagingDir, outPath, ct);
            if (run.ExitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(run.StdErr) ? run.StdOut.Trim() : run.StdErr.Trim();
                return new PackResult(null, new[]
                {
                    new Diagnostic(DiagnosticSeverity.Error, "SIG0110",
                        $"MakeAppx.exe exited {run.ExitCode}: {detail}",
                        SourceLocation.Unknown,
                        "https://docs.sigil.build/diagnostics/SIG0110"),
                });
            }

            var sha = ManifestHasher.Sha256(outPath);
            var size = new FileInfo(outPath).Length;

            var diagnostics = new List<Diagnostic>();
            if (manifest.Package?.Msix?.RunWack == true)
            {
                if (!WackRunner.TryFromInstalled(out var wack))
                {
                    diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "SIG0111",
                        "runWack=true but the Windows App Certification Kit (appcert.exe) is not installed; skipping WACK validation",
                        SourceLocation.Unknown,
                        "https://docs.sigil.build/diagnostics/SIG0111"));
                }
                else
                {
                    var reportPath = Path.Combine(options.OutputDirectory, Path.GetFileNameWithoutExtension(fileName) + ".wack-report.xml");
                    var wackResult = await wack.RunAsync(outPath, reportPath, ct);
                    if (wackResult.ExitCode != 0)
                    {
                        diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "SIG0112",
                            $"Windows App Certification Kit reported failures (exit {wackResult.ExitCode}); see {wackResult.ReportPath}",
                            SourceLocation.Unknown,
                            "https://docs.sigil.build/diagnostics/SIG0112"));
                        return new PackResult(null, diagnostics);
                    }
                }
            }

            return new PackResult(new PackedArtifact(outPath, sha, size), diagnostics);
        }
        finally
        {
            try { Directory.Delete(stagingDir, recursive: true); } catch (Exception) { /* best-effort cleanup */ }
        }
    }

    private static void CopyTree(string source, string dest, IReadOnlyList<string>? include, IReadOnlyList<string>? exclude)
    {
        foreach (var f in DeterministicFileWalker.Walk(source, include, exclude))
        {
            var target = Path.Combine(dest, f.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(f.AbsolutePath, target, overwrite: true);
        }
    }

    private static void CreatePlaceholderAssets(string assetsDir)
    {
        // Minimal 1×1 transparent PNG so MakeAppx doesn't reject the package.
        var transparent = new byte[]
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D,
            0x49, 0x48, 0x44, 0x52, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
            0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4, 0x89, 0x00, 0x00, 0x00,
            0x0D, 0x49, 0x44, 0x41, 0x54, 0x78, 0x9C, 0x62, 0x00, 0x01, 0x00, 0x00,
            0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00, 0x00, 0x00, 0x00, 0x49,
            0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82,
        };
        foreach (var name in new[] { "Square44x44Logo.png", "Square150x150Logo.png", "Wide310x150Logo.png", "StoreLogo.png" })
            File.WriteAllBytes(Path.Combine(assetsDir, name), transparent);
    }
}
