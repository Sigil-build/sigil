namespace SigilBuild.Wrapper.Engine;

using System;
using System.Runtime.Versioning;
using Microsoft.Win32;

/// <summary>
/// Win32-registry plumbing shared by <c>RegistryWriteStep</c>,
/// <c>RegistryDeleteValueStep</c>, <c>RegistryDeleteKeyStep</c>, and the
/// <c>registry_exists(...)</c> expression function.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class RegistryHelper
{
    /// <summary>
    /// Map a manifest hive string (<c>HKLM</c>, <c>HKEY_LOCAL_MACHINE</c>, …)
    /// to <see cref="RegistryHive"/>.
    /// </summary>
    public static RegistryHive ParseHive(string? hive) => hive switch
    {
        "HKLM" or "HKEY_LOCAL_MACHINE"  => RegistryHive.LocalMachine,
        "HKCU" or "HKEY_CURRENT_USER"   => RegistryHive.CurrentUser,
        "HKCR" or "HKEY_CLASSES_ROOT"   => RegistryHive.ClassesRoot,
        "HKU"  or "HKEY_USERS"          => RegistryHive.Users,
        "HKCC" or "HKEY_CURRENT_CONFIG" => RegistryHive.CurrentConfig,
        _ => throw new ArgumentException($"unknown registry hive '{hive}'"),
    };

    /// <summary>
    /// Map the manifest <c>view:</c> field to <see cref="RegistryView"/>.
    /// <c>null</c>, empty, or <c>native</c> → <see cref="RegistryView.Default"/>;
    /// <c>32</c> / <c>64</c> map to the explicit WoW64 views.
    /// </summary>
    public static RegistryView ParseView(string? view) => view switch
    {
        null or "" or "native" => RegistryView.Default,
        "32" => RegistryView.Registry32,
        "64" => RegistryView.Registry64,
        _ => throw new ArgumentException($"unknown registry view '{view}' (expected 32, 64, or native)"),
    };

    /// <summary>
    /// Map the manifest <c>type:</c> field (<c>REG_SZ</c>, <c>REG_DWORD</c>, …)
    /// to <see cref="RegistryValueKind"/>.
    /// </summary>
    public static RegistryValueKind ParseValueKind(string? type) => type switch
    {
        "REG_SZ"        => RegistryValueKind.String,
        "REG_DWORD"     => RegistryValueKind.DWord,
        "REG_QWORD"     => RegistryValueKind.QWord,
        "REG_MULTI_SZ"  => RegistryValueKind.MultiString,
        "REG_EXPAND_SZ" => RegistryValueKind.ExpandString,
        "REG_BINARY"    => RegistryValueKind.Binary,
        _ => throw new ArgumentException($"unknown registry value kind '{type}'"),
    };

    /// <summary>
    /// Inverse of <see cref="ParseValueKind"/>: serialize the runtime kind
    /// back to the manifest token used by rollback records.
    /// </summary>
    public static string ValueKindToString(RegistryValueKind k) => k switch
    {
        RegistryValueKind.String       => "REG_SZ",
        RegistryValueKind.DWord        => "REG_DWORD",
        RegistryValueKind.QWord        => "REG_QWORD",
        RegistryValueKind.MultiString  => "REG_MULTI_SZ",
        RegistryValueKind.ExpandString => "REG_EXPAND_SZ",
        RegistryValueKind.Binary       => "REG_BINARY",
        _                              => "REG_NONE",
    };

    /// <summary>
    /// Probe used by the <c>registry_exists(hive, key, name)</c> expression
    /// function. When <paramref name="name"/> is null/empty, returns true if
    /// the key exists at all; otherwise true only if the named value is set.
    /// Always returns false on non-Windows hosts. Failure to open the key
    /// (e.g. ACL denial) is treated as "not visible" rather than an error.
    /// </summary>
    public static bool Exists(string? hive, string? key, string? name)
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (string.IsNullOrEmpty(hive) || string.IsNullOrEmpty(key)) return false;
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(ParseHive(hive), RegistryView.Default);
            using var sub = baseKey.OpenSubKey(key, writable: false);
            if (sub is null) return false;
            return string.IsNullOrEmpty(name) || sub.GetValue(name) is not null;
        }
#pragma warning disable CA1031 // Best-effort probe: any registry failure means "not visible".
        catch
        {
            return false;
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Capture the prior state of <paramref name="name"/> under
    /// <c>(hive, key)</c> so a rollback can restore it. When the value (or
    /// its containing key) does not exist, returns
    /// <c>(None, null, PreviouslyAbsent: true)</c>; the caller MUST treat
    /// rollback as "delete the value if we created it".
    /// </summary>
    public static (RegistryValueKind Kind, object? Value, bool PreviouslyAbsent) Snapshot(
        RegistryHive hive, string key, string name, RegistryView view)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using var sub = baseKey.OpenSubKey(key, writable: false);
        if (sub is null) return (RegistryValueKind.None, null, PreviouslyAbsent: true);
        var value = sub.GetValue(name);
        if (value is null) return (RegistryValueKind.None, null, PreviouslyAbsent: true);
        return (sub.GetValueKind(name), value, PreviouslyAbsent: false);
    }
}
