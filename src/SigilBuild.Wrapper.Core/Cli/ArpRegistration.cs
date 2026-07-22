namespace SigilBuild.Wrapper.Cli;

using System;
using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Win32;
using SigilBuild.Core.Manifest;

/// <summary>
/// Writes and removes the per-app entry under
/// <c>…\Microsoft\Windows\CurrentVersion\Uninstall</c> so the installation
/// surfaces in Windows' "Add or Remove Programs" UI. The hive follows the
/// install scope (T12): HKLM for a per-machine install, HKCU for a per-user
/// install. The wrapper exe (the copied <c>uninstall.exe</c>) is the binary the
/// OS calls back into for uninstall — see <see cref="BuildUninstallString"/>.
/// </summary>
/// <remarks>
/// Writes target the 64-bit registry view by default (the .NET registry
/// API on a 64-bit process does the right thing for us). 32-bit
/// installations writing into the WoW64 view are deferred to a later
/// task — Task 19 only covers the headline x64 case.
/// </remarks>
[SupportedOSPlatform("windows")]
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage(Justification = "Writes/removes …\\Microsoft\\Windows\\CurrentVersion\\Uninstall entries; exercised only via Windows installer integration tests.")]
internal static class ArpRegistration
{
    private const string UninstallKeyRoot =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>
    /// The scope-correct ARP root key: HKLM for a per-machine install (visible to
    /// all users, requires elevation), HKCU for a per-user install (no elevation).
    /// </summary>
    private static RegistryKey HiveFor(InstallScope scope) =>
        scope == InstallScope.Machine ? Registry.LocalMachine : Registry.CurrentUser;

    /// <summary>Bundle of fields the OS reads from a per-app ARP key.</summary>
    /// <param name="AppId">Subkey name under <see cref="UninstallKeyRoot"/>.</param>
    /// <param name="DisplayName">Shown to the end user as the program name.</param>
    /// <param name="DisplayVersion">Shown next to the program name in ARP.</param>
    /// <param name="Publisher">Vendor string.</param>
    /// <param name="UninstallString">Full command line the OS runs on "Uninstall".</param>
    /// <param name="EstimatedSizeBytes">Total install footprint in bytes;
    /// the OS converts to KB when displaying the size column.</param>
    /// <param name="InstallLocation">The resolved install directory, written as the
    /// standard ARP <c>InstallLocation</c> value. Read back by
    /// <see cref="SigilBuild.Wrapper.Engine.InstalledStateResolver"/> to resolve the
    /// prior install dir for a P3 upgrade. Empty → the value is not written.</param>
    public sealed record Entry(
        string AppId,
        string DisplayName,
        string DisplayVersion,
        string Publisher,
        string UninstallString,
        long EstimatedSizeBytes,
        string InstallLocation = "");

    /// <summary>
    /// Create or update the ARP key for <paramref name="entry"/> in the
    /// scope-correct hive (T12). A per-machine install writes HKLM (requires the
    /// elevated relaunch); a per-user install writes HKCU (no elevation).
    /// </summary>
    public static void Register(Entry entry, InstallScope scope)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var keyPath = $@"{UninstallKeyRoot}\{entry.AppId}";
        using var key = HiveFor(scope).CreateSubKey(keyPath, writable: true)
            ?? throw new InvalidOperationException($"could not create ARP key '{keyPath}'");

        key.SetValue("DisplayName",     entry.DisplayName);
        key.SetValue("DisplayVersion",  entry.DisplayVersion);
        key.SetValue("Publisher",       entry.Publisher);
        key.SetValue("UninstallString", entry.UninstallString);
        // Standard ARP InstallLocation — surfaces the install dir to Windows and lets
        // a later P3 upgrade recover the prior install directory (InstalledStateResolver).
        if (!string.IsNullOrEmpty(entry.InstallLocation))
        {
            key.SetValue("InstallLocation", entry.InstallLocation);
        }
        // EstimatedSize is documented as a DWORD in KB.
        var sizeKb = (int)Math.Min(int.MaxValue, Math.Max(0, entry.EstimatedSizeBytes / 1024));
        key.SetValue("EstimatedSize",   sizeKb, RegistryValueKind.DWord);
        key.SetValue("InstallDate",     DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        key.SetValue("NoModify",        1, RegistryValueKind.DWord);
        key.SetValue("NoRepair",        1, RegistryValueKind.DWord);
    }

    /// <summary>
    /// Best-effort delete of the ARP key for <paramref name="appId"/> from the
    /// scope-correct hive (T12). Missing keys are silently ignored.
    /// </summary>
    public static void Remove(string appId, InstallScope scope)
    {
        ArgumentException.ThrowIfNullOrEmpty(appId);
        var keyPath = $@"{UninstallKeyRoot}\{appId}";
#pragma warning disable CA1031 // Best-effort; a leftover ARP entry is preferable to a crash on uninstall.
        try
        {
            HiveFor(scope).DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        }
        catch
        {
            // Best-effort.
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Construct the <c>UninstallString</c> the OS calls when the user clicks
    /// "Uninstall" in Add or Remove Programs. Points at <em>this</em> wrapper
    /// exe with <c>/S /Uninstall</c> and the scope flag appended (T12): a
    /// per-machine install emits <c>/allusers</c>, a per-user install
    /// <c>/currentuser</c>, so the uninstall re-resolves to the same scope it was
    /// installed with regardless of the manifest default (HKLM vs HKCU ARP,
    /// %ProgramData% vs %LocalAppData% state, elevation).
    /// </summary>
    public static string BuildUninstallString(string wrapperExePath, InstallScope scope)
    {
        ArgumentException.ThrowIfNullOrEmpty(wrapperExePath);
        var scopeFlag = scope == InstallScope.Machine ? "/allusers" : "/currentuser";
        return $"\"{wrapperExePath}\" /S /Uninstall {scopeFlag}";
    }
}
