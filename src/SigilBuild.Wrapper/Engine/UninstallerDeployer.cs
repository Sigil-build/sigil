using System;
using System.IO;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace SigilBuild.Wrapper.Engine;

/// <summary>
/// At install time, drop the embedded <c>uninstaller.exe</c>
/// (<c>SIGIL_UNINSTALLER_V1</c> resource, populated by the packager when the
/// manifest has an <c>uninstall:</c> block) to a known location inside the
/// install dir and wire the ARP <c>UninstallString</c> to point at it.
/// </summary>
/// <remarks>
/// <para>
/// This complements <see cref="SigilBuild.Wrapper.Cli.ArpRegistration"/>: that
/// class writes the ARP entry whose <c>UninstallString</c> by default points
/// back at <c>setup.exe /S /Uninstall</c>. When an embedded uninstaller is
/// available, this deployer overrides that field so end-users see the NSIS-
/// parity behaviour: a sibling <c>uninstaller.exe</c> in the install dir that
/// they can launch directly from Explorer, and an ARP "Uninstall" button that
/// invokes the same binary.
/// </para>
/// <para>
/// Deploy failures are deliberately soft: the wrapper's own <c>/Uninstall</c>
/// mode still works against the persisted journal even without a sibling
/// uninstaller.exe, so a transient I/O error here must not abort an otherwise
/// successful install.
/// </para>
/// </remarks>
internal static class UninstallerDeployer
{
    private const string UninstallerFileName = "uninstaller.exe";

    /// <summary>
    /// Best-effort deploy. Returns the absolute path to the deployed
    /// uninstaller, or <c>null</c> if no uninstaller is embedded or the
    /// deploy failed.
    /// </summary>
    public static string? TryDeploy(string installDir, string appId)
    {
        ArgumentException.ThrowIfNullOrEmpty(installDir);
        ArgumentException.ThrowIfNullOrEmpty(appId);

        var bytes = WrapperBlob.LoadUninstallerExeBytes();
        if (bytes is null || bytes.Length == 0)
        {
            WrapperLog.Info("UninstallerDeployer: no SIGIL_UNINSTALLER_V1 resource embedded — skipping");
            return null;
        }

        try
        {
            Directory.CreateDirectory(installDir);
            var path = Path.Combine(installDir, UninstallerFileName);
            File.WriteAllBytes(path, bytes);
            WrapperLog.Info($"UninstallerDeployer: wrote {bytes.Length:N0} bytes to {path}");

            if (OperatingSystem.IsWindows())
            {
                WriteArpUninstallString(appId, path);
            }
            return path;
        }
#pragma warning disable CA1031 // Soft failure: keep the install succeeding even if the sibling drop / ARP rewrite fails.
        catch (Exception ex)
        {
            WrapperLog.Error($"UninstallerDeployer: deploy failed for {installDir}", ex);
            return null;
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Overwrite the ARP <c>UninstallString</c> (and add
    /// <c>QuietUninstallString</c>) to point at the freshly-deployed sibling
    /// <c>uninstaller.exe</c>. Called after
    /// <see cref="SigilBuild.Wrapper.Cli.ArpRegistration.Register"/> has
    /// already created the parent key, so the subkey is guaranteed to exist —
    /// we just patch two values.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void WriteArpUninstallString(string appId, string uninstallerPath)
    {
        // HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\<AppId>
        //   UninstallString      = "<install_dir>\uninstaller.exe"
        //   QuietUninstallString = "<install_dir>\uninstaller.exe" /S
        using var key = Registry.LocalMachine.CreateSubKey(
            $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{appId}", writable: true);
        if (key is null)
        {
            WrapperLog.Error($"UninstallerDeployer: could not open/create ARP key for {appId}");
            return;
        }
        var quoted = $"\"{uninstallerPath}\"";
        key.SetValue("UninstallString", quoted, RegistryValueKind.String);
        key.SetValue("QuietUninstallString", $"{quoted} /S", RegistryValueKind.String);
        WrapperLog.Info($"UninstallerDeployer: ARP UninstallString = {quoted}");
    }
}
