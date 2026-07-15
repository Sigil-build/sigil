namespace SigilBuild.Wrapper.Engine;

using System;
using System.IO;
using System.Runtime.Versioning;
using Microsoft.Win32;
using SigilBuild.Core.Manifest;

/// <summary>
/// Reads the scope-correct Add/Remove-Programs entry for an app id to resolve the
/// installed state feeding the P3 version-aware upgrade decision (gap G3): the
/// installed <c>DisplayVersion</c>, the prior install directory, and the prior
/// <c>uninstall.exe</c> path. Probes the tentative scope's hive first, then the
/// other hive, so an existing install's scope is discovered even when it differs
/// from an auto-resolved scope (the existing scope then wins — see
/// <see cref="InstallSession"/>). Read-only; returns <see cref="UpgradeState.None"/>
/// off Windows or when no entry exists in either hive.
/// </summary>
public static class InstalledStateResolver
{
    // Mirrors ArpRegistration.UninstallKeyRoot (private there): the ARP subkey layout
    // this reads back is the exact layout ArpRegistration.Register writes.
    private const string UninstallKeyRoot =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>
    /// Resolve the installed state for <paramref name="appId"/>. <paramref name="tentativeScope"/>
    /// only sets the hive probe order (so a same-scope install resolves to its own hive);
    /// both hives are always searched, so a prior install in the other scope is still found.
    /// </summary>
    public static UpgradeState Resolve(string appId, InstallScope tentativeScope)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(appId))
        {
            return UpgradeState.None;
        }

        var order = tentativeScope == InstallScope.Machine
            ? new[] { InstallScope.Machine, InstallScope.User }
            : new[] { InstallScope.User, InstallScope.Machine };

        foreach (var scope in order)
        {
            var state = TryReadFromHive(appId, scope);
            if (state is not null)
            {
                return state;
            }
        }

        return UpgradeState.None;
    }

    [SupportedOSPlatform("windows")]
    private static UpgradeState? TryReadFromHive(string appId, InstallScope scope)
    {
        var hive = scope == InstallScope.Machine ? Registry.LocalMachine : Registry.CurrentUser;
        var keyPath = $@"{UninstallKeyRoot}\{appId}";
#pragma warning disable CA1031 // Best-effort probe: any registry failure means "not installed here".
        try
        {
            using var key = hive.OpenSubKey(keyPath, writable: false);
            if (key is null)
            {
                return null;
            }

            var version = key.GetValue("DisplayVersion") as string ?? string.Empty;
            var installLocation = key.GetValue("InstallLocation") as string ?? string.Empty;
            var uninstallString = key.GetValue("UninstallString") as string ?? string.Empty;

            var uninstallExe = ParseUninstallExe(uninstallString);
            var installDir = !string.IsNullOrEmpty(installLocation)
                ? installLocation
                : (!string.IsNullOrEmpty(uninstallExe)
                    ? Path.GetDirectoryName(uninstallExe) ?? string.Empty
                    : string.Empty);

            // If the UninstallString gave no exe but we know the dir, assume the
            // conventional {dir}\uninstall.exe (T15 always lands it there).
            if (string.IsNullOrEmpty(uninstallExe) && !string.IsNullOrEmpty(installDir))
            {
                uninstallExe = Path.Combine(installDir, InstallSurvivability.UninstallerFileName);
            }

            return new UpgradeState(
                Found: true,
                InstalledVersion: version,
                PriorInstallDir: installDir,
                PriorUninstallExe: uninstallExe,
                FoundScope: scope);
        }
        catch
        {
            return null;
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Extract the executable path from an ARP <c>UninstallString</c>. Sigil writes
    /// <c>"&lt;dir&gt;\uninstall.exe" /S /Uninstall &lt;scope&gt;</c> (see
    /// <see cref="Cli.ArpRegistration.BuildUninstallString"/>), so the exe is the first
    /// quoted token — or, for a degenerate unquoted form, the first whitespace-delimited
    /// token. Returns <c>""</c> when none can be parsed.
    /// </summary>
    internal static string ParseUninstallExe(string? uninstallString)
    {
        if (string.IsNullOrWhiteSpace(uninstallString))
        {
            return string.Empty;
        }

        var s = uninstallString.Trim();
        if (s[0] == '"')
        {
            var end = s.IndexOf('"', 1);
            return end > 1 ? s.Substring(1, end - 1) : string.Empty;
        }

        // Unquoted: take up to the first space. BuildUninstallString always quotes the
        // exe (paths can contain spaces), so this only handles a legacy/hand-written form.
        var space = s.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? s : s.Substring(0, space);
    }
}
