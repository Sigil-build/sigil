using System;
using System.IO;
using SigilBuild.Core.Manifest;

namespace SigilBuild.Packaging.Installer;

public static class InstallerHostBundler
{
    /// <summary>
    /// Copies the AOT-published installer.exe into the MSIX staging dir and writes
    /// BrandTokens.g.json next to it so the host loads branding at startup.
    /// </summary>
    public static void Bundle(SigilManifest manifest, string installerExeSource, string stagingDir)
    {
        if (!File.Exists(installerExeSource))
            throw new FileNotFoundException($"installer host binary not found at {installerExeSource}");

        File.Copy(installerExeSource, Path.Combine(stagingDir, "installer.exe"), overwrite: true);

        var tokens = BrandTokenEmitter.Emit(manifest);
        File.WriteAllText(Path.Combine(stagingDir, "BrandTokens.g.json"), tokens);
    }
}
