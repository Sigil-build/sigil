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

[SupportedOSPlatform("windows")]
internal static class TestRegistry
{
    public static TestRegistryKey CreateScratchKey() => new();
}
