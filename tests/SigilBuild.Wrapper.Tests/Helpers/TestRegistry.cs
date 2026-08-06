namespace SigilBuild.Wrapper.Tests.Helpers;

using System;
using System.Runtime.Versioning;
using Microsoft.Win32;

/// <summary>
/// Disposable scratch key under <c>HKCU\Software\Sigil-test\&lt;guid&gt;</c>
/// for registry-step tests. Avoids HKLM (admin required) and avoids
/// touching real production paths under HKCU. Cleans up the entire subtree
/// on dispose, even if the test left rollback-deleted state behind.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class TestRegistryKey : IDisposable
{
    /// <summary>Path relative to <c>HKEY_CURRENT_USER</c>.</summary>
    public string Path { get; }

    public TestRegistryKey()
    {
        Path = @"Software\Sigil-test\" + Guid.NewGuid().ToString("N");
        using var k = Registry.CurrentUser.CreateSubKey(Path)
            ?? throw new InvalidOperationException($"could not create scratch key {Path}");
    }

    public bool Exists()
    {
        using var k = Registry.CurrentUser.OpenSubKey(Path);
        return k is not null;
    }

    public object? GetValue(string name)
    {
        using var k = Registry.CurrentUser.OpenSubKey(Path);
        return k?.GetValue(name);
    }

    public RegistryValueKind GetValueKind(string name)
    {
        using var k = Registry.CurrentUser.OpenSubKey(Path)
            ?? throw new InvalidOperationException($"key {Path} does not exist");
        return k.GetValueKind(name);
    }

    public void SetValue(string name, object value)
    {
        using var k = Registry.CurrentUser.OpenSubKey(Path, writable: true)
            ?? throw new InvalidOperationException($"key {Path} does not exist");
        k.SetValue(name, value);
    }

    public void SetValue(string name, object value, RegistryValueKind kind)
    {
        using var k = Registry.CurrentUser.OpenSubKey(Path, writable: true)
            ?? throw new InvalidOperationException($"key {Path} does not exist");
        k.SetValue(name, value, kind);
    }

    public bool HasValue(string name) => GetValue(name) is not null;

    public void Dispose()
    {
#pragma warning disable CA1031 // Best-effort scratch-key cleanup; ignore residual ACL/race issues.
        try { Registry.CurrentUser.DeleteSubKeyTree(Path, throwOnMissingSubKey: false); }
        catch { /* best-effort */ }
#pragma warning restore CA1031
    }
}

/// <summary>
/// A planted Add/Remove-Programs entry under
/// <c>HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\&lt;appId&gt;</c> — the
/// exact key register row R2's attacker writes, and the exact key
/// <c>InstalledStateResolver</c> must not read on a machine-scope resolve.
/// </summary>
/// <remarks>
/// <para>
/// <b>HKCU only, by construction.</b> There is no HKLM variant and there must never
/// be one: writing the machine ARP hive needs elevation and would mutate the real
/// machine's installed-programs list. The attack this models does not need HKLM —
/// that it works from the user's own hive is the entire finding.
/// </para>
/// <para>
/// The caller supplies a unique <c>appId</c> (a GUID-suffixed
/// <c>sigil.test.*</c>), so the subkey can never collide with a real installed
/// product, and <see cref="Dispose"/> deletes the subtree unconditionally.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class PlantedUninstallEntry : IDisposable
{
    private const string UninstallKeyRoot =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>Path of the planted key relative to <c>HKEY_CURRENT_USER</c>.</summary>
    public string Path { get; }

    internal PlantedUninstallEntry(string appId, string displayVersion, string uninstallString)
    {
        Path = UninstallKeyRoot + @"\" + appId;
        using var key = Registry.CurrentUser.CreateSubKey(Path)
            ?? throw new InvalidOperationException($"could not create planted ARP key {Path}");
        key.SetValue("DisplayVersion", displayVersion);
        key.SetValue("UninstallString", uninstallString);
    }

    public void Dispose()
    {
#pragma warning disable CA1031 // Best-effort fixture cleanup; ignore residual ACL/race issues.
        try { Registry.CurrentUser.DeleteSubKeyTree(Path, throwOnMissingSubKey: false); }
        catch { /* best-effort */ }
#pragma warning restore CA1031
    }
}

[SupportedOSPlatform("windows")]
internal static class TestRegistry
{
    public static TestRegistryKey CreateScratchKey() => new();

    /// <summary>
    /// Plant an ARP entry for <paramref name="appId"/> in the <b>user</b> hive.
    /// See <see cref="PlantedUninstallEntry"/> for why this is HKCU-only.
    /// </summary>
    public static PlantedUninstallEntry PlantUserUninstallEntry(
        string appId, string displayVersion, string uninstallString) =>
        new(appId, displayVersion, uninstallString);
}
