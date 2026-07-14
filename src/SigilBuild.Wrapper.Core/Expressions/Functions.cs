using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace SigilBuild.Wrapper.Expressions;

/// <summary>
/// Closed function table. Anything not present here throws
/// <see cref="ExpressionException"/> at evaluation time.
///
/// SECURITY: every function is pure w.r.t. side effects and does only
/// bounded, read-only I/O (a single registry/file/env read) — never a shell
/// out, reflection, network call, or any write. Do NOT add functions that
/// breach that envelope. Anyone proposing such a feature must amend ADR-008
/// first — see docs/architecture/adr-008-expression-policy.md §1 (closed
/// function catalog + admission criteria). Every data-retrieval function
/// returns <c>""</c> on the absent/denied path rather than throwing (total).
/// </summary>
internal static class Functions
{
    public static readonly IReadOnlyDictionary<string, Func<object?[], object?>> Table
        = new Dictionary<string, Func<object?[], object?>>(StringComparer.Ordinal)
        {
            // defined / empty get special handling in Evaluator.CallFunction —
            // they need to observe missing identifiers as "absent" rather than
            // a hard parse error. By the time this lambda runs, the argument
            // has already been resolved (or replaced with null on the
            // unknown-identifier path).
            ["defined"] = a => a[0] is not null,

            ["empty"] = a => a[0] is null
                || (a[0] is string s && s.Length == 0)
                || (a[0] is ICollection col && col.Count == 0),

            ["version_gte"] = a => SigilBuild.Wrapper.Engine.VersionComparison
                .Compare(ToStringOrNull(a[0]), ToStringOrNull(a[1])) >= 0,

            ["os_version"] = _ => Environment.OSVersion.Version.ToString(),

            ["arch"] = _ => RuntimeInformation.ProcessArchitecture
                .ToString().ToLowerInvariant(),

            // CurrentUICulture.Name is "" under InvariantGlobalization=true
            // but the function is still callable; tests assert non-empty for
            // os_version() and arch() only.
            ["locale"] = _ => CultureInfo.CurrentUICulture.Name,

            ["file_exists"] = a => File.Exists(ToStringOrNull(a[0])),

            ["registry_exists"] = a => RegistryExists(
                ToStringOrNull(a[0]),
                ToStringOrNull(a[1]),
                ToStringOrNull(a[2])),

            // --- P1 data-retrieval functions (ADR-008 §1.3). All return a
            //     string, "" when absent/denied, read-only, AOT-safe. These are
            //     the declarative equivalents of NSIS ReadRegStr / Inno
            //     RegQueryStringValue / WiX RegistrySearch. ---

            ["registry_read"] = a => RegistryRead(
                ToStringOrNull(a[0]),
                ToStringOrNull(a[1]),
                ToStringOrNull(a[2])),

            ["env"] = a => Environment.GetEnvironmentVariable(ToStringOrNull(a[0]) ?? string.Empty)
                ?? string.Empty,

            ["file_version"] = a => FileVersion(ToStringOrNull(a[0])),

            ["installed_version"] = a => InstalledVersion(ToStringOrNull(a[0])),
        };

    // Indirection so the analyzer sees an OS guard for the Windows-only
    // `Engine.RegistryHelper.Exists`. The wrapper itself only ships on
    // Windows (RID=win-x64), but Functions.cs is platform-agnostic.
    private static bool RegistryExists(string? hive, string? key, string? name)
    {
        if (!OperatingSystem.IsWindows()) return false;
        return SigilBuild.Wrapper.Engine.RegistryHelper.Exists(hive, key, name);
    }

    // Same OS-guard indirection as RegistryExists for the Windows-only
    // `RegistryHelper.Read`. Returns "" on non-Windows and on any read failure.
    private static string RegistryRead(string? hive, string? key, string? name)
    {
        if (!OperatingSystem.IsWindows()) return string.Empty;
        return SigilBuild.Wrapper.Engine.RegistryHelper.Read(hive, key, name);
    }

    // FileVersionInfo is AOT-safe (a thin wrapper over the OS version APIs; no
    // reflection). Returns "" when the path is empty, the file is absent, or the
    // file carries no version resource. Never loads or executes the file.
    private static string FileVersion(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return string.Empty;
        try
        {
            return System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileVersion
                ?? string.Empty;
        }
        catch (FileNotFoundException)
        {
            // Raced deletion between File.Exists and GetVersionInfo — treat as absent.
            return string.Empty;
        }
    }

    // Reads this app's own Add/Remove-Programs DisplayVersion (feeds P3 upgrade
    // logic). The install scope is not known at eval time, so probe the machine
    // hive first, then the per-user hive — mirroring ArpRegistration's
    // scope-correct write layout (…\CurrentVersion\Uninstall\<app_id>). "" when
    // the app is not installed under either hive.
    private static string InstalledVersion(string? appId)
    {
        if (!OperatingSystem.IsWindows()) return string.Empty;
        if (string.IsNullOrEmpty(appId)) return string.Empty;

        var key = ArpUninstallKeyRoot + "\\" + appId;
        var machine = SigilBuild.Wrapper.Engine.RegistryHelper.Read("HKLM", key, "DisplayVersion");
        return machine.Length > 0
            ? machine
            : SigilBuild.Wrapper.Engine.RegistryHelper.Read("HKCU", key, "DisplayVersion");
    }

    // Mirrors ArpRegistration.UninstallKeyRoot (private there); the ARP subkey
    // layout the installed_version() probe reads back.
    private const string ArpUninstallKeyRoot =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private static string? ToStringOrNull(object? value) =>
        value is null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
}
