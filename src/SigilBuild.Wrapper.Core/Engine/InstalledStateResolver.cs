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
/// <c>uninstall.exe</c> path. Read-only; returns <see cref="UpgradeState.None"/>
/// off Windows or when no entry is found.
/// </summary>
/// <remarks>
/// An <b>unelevated user</b>-scope resolve probes HKCU first and then falls back to
/// HKLM, so an existing machine install is still discovered and its scope wins (see
/// <see cref="InstallSession"/>); reading a hive the caller cannot write is safe.
/// A <b>machine</b>-scope resolve, or <b>any</b> resolve in an elevated process,
/// probes HKLM and nothing else — see <see cref="ScopeProbeOrder"/> for why the
/// mirrored fallback was a privilege escalation, not a convenience.
/// </remarks>
public static class InstalledStateResolver
{
    // Mirrors ArpRegistration.UninstallKeyRoot (private there): the ARP subkey layout
    // this reads back is the exact layout ArpRegistration.Register writes.
    private const string UninstallKeyRoot =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>
    /// Resolve the installed state for <paramref name="appId"/>.
    /// <paramref name="tentativeScope"/> selects the hives probed and their order —
    /// see <see cref="ScopeProbeOrder"/>.
    /// </summary>
    public static UpgradeState Resolve(string appId, InstallScope tentativeScope)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(appId))
        {
            return UpgradeState.None;
        }

        foreach (var scope in ScopeProbeOrder(tentativeScope, Elevation.IsProcessElevated()))
        {
            var state = TryReadFromHive(appId, scope);
            if (state is not null)
            {
                return state;
            }
        }

        return UpgradeState.None;
    }

    /// <summary>
    /// The hives a resolve at <paramref name="tentativeScope"/> may read, in order.
    /// Pure — <paramref name="elevated"/> is passed in rather than probed — so both
    /// branches are testable from a single unelevated test run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// R2. This was symmetric: machine scope probed HKLM and then fell back to HKCU.
    /// Everything read out of that key is acted on by an <em>already elevated</em>
    /// process — in particular <c>UninstallString</c>, which
    /// <see cref="ParseUninstallExe"/> turns into an exe path that
    /// <see cref="InstallSession"/> spawns. HKCU is writable by the unprivileged user,
    /// so that fallback let any standard user plant an ARP entry with a low
    /// <c>DisplayVersion</c> (classified as an upgrade) and an <c>UninstallString</c>
    /// aimed at their own binary, then wait for the next admin-approved run of the
    /// publisher's legitimate installer to run it as administrator.
    /// </para>
    /// <para>
    /// The condition is <b>machine scope OR an elevated process</b>, which is how
    /// register row R2 words it, and the second half is not redundant: a
    /// <em>user</em>-scope run can be elevated — launched with Run as administrator,
    /// started from an already-elevated shell, or deployed by Intune / SCCM running as
    /// SYSTEM. Keying on scope alone left every one of those probing HKCU and reaching
    /// the same elevated spawn, with the trust gate in
    /// <see cref="InstallSession.IsPriorUninstallerTrusted"/> as the sole remaining
    /// defence. Two independent gates were the design; one is not.
    /// </para>
    /// <para>
    /// The unelevated user-scope fallback to HKLM stays: a per-user install reading the
    /// machine hive is reading a hive it cannot write, which is what makes a cross-scope
    /// upgrade discoverable without trusting attacker-writable data. The direction of
    /// the asymmetry is the whole fix — do not "restore the symmetry".
    /// </para>
    /// </remarks>
    internal static InstallScope[] ScopeProbeOrder(InstallScope tentativeScope, bool elevated) =>
        tentativeScope == InstallScope.Machine || elevated
            ? new[] { InstallScope.Machine }
            : new[] { InstallScope.User, InstallScope.Machine };

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
