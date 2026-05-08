namespace SigilBuild.Wrapper.Cli;

using System;
using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Win32;

/// <summary>
/// Writes and removes the per-app entry under
/// <c>HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall</c> so the
/// installation surfaces in Windows' "Add or Remove Programs" UI. The
/// wrapper exe is the same binary the OS calls back into for uninstall —
/// see <see cref="BuildUninstallString"/>.
/// </summary>
/// <remarks>
/// Writes target the 64-bit registry view by default (the .NET registry
/// API on a 64-bit process does the right thing for us). 32-bit
/// installations writing into the WoW64 view are deferred to a later
/// task — Task 19 only covers the headline x64 case.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class ArpRegistration
{
    private const string UninstallKeyRoot =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>Bundle of fields the OS reads from a per-app ARP key.</summary>
    /// <param name="AppId">Subkey name under <see cref="UninstallKeyRoot"/>.</param>
    /// <param name="DisplayName">Shown to the end user as the program name.</param>
    /// <param name="DisplayVersion">Shown next to the program name in ARP.</param>
    /// <param name="Publisher">Vendor string.</param>
    /// <param name="UninstallString">Full command line the OS runs on "Uninstall".</param>
    /// <param name="EstimatedSizeBytes">Total install footprint in bytes;
    /// the OS converts to KB when displaying the size column.</param>
    public sealed record Entry(
        string AppId,
        string DisplayName,
        string DisplayVersion,
        string Publisher,
        string UninstallString,
        long EstimatedSizeBytes);

    /// <summary>
    /// Create or update the ARP key for <paramref name="entry"/>.
    /// Requires HKLM write access — the wrapper's UAC manifest already
    /// elevates the install run (Task 18+), so the call site is safe.
    /// </summary>
    public static void Register(Entry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var keyPath = $@"{UninstallKeyRoot}\{entry.AppId}";
        using var key = Registry.LocalMachine.CreateSubKey(keyPath, writable: true)
            ?? throw new InvalidOperationException($"could not create ARP key '{keyPath}'");

        key.SetValue("DisplayName",     entry.DisplayName);
        key.SetValue("DisplayVersion",  entry.DisplayVersion);
        key.SetValue("Publisher",       entry.Publisher);
        key.SetValue("UninstallString", entry.UninstallString);
        // EstimatedSize is documented as a DWORD in KB.
        var sizeKb = (int)Math.Min(int.MaxValue, Math.Max(0, entry.EstimatedSizeBytes / 1024));
        key.SetValue("EstimatedSize",   sizeKb, RegistryValueKind.DWord);
        key.SetValue("InstallDate",     DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        key.SetValue("NoModify",        1, RegistryValueKind.DWord);
        key.SetValue("NoRepair",        1, RegistryValueKind.DWord);
    }

    /// <summary>
    /// Best-effort delete of the ARP key for <paramref name="appId"/>.
    /// Missing keys are silently ignored.
    /// </summary>
    public static void Remove(string appId)
    {
        ArgumentException.ThrowIfNullOrEmpty(appId);
        var keyPath = $@"{UninstallKeyRoot}\{appId}";
#pragma warning disable CA1031 // Best-effort; a leftover ARP entry is preferable to a crash on uninstall.
        try
        {
            Registry.LocalMachine.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
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
    /// exe with <c>/S /Uninstall</c> appended.
    /// </summary>
    public static string BuildUninstallString(string wrapperExePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(wrapperExePath);
        return $"\"{wrapperExePath}\" /S /Uninstall";
    }
}
