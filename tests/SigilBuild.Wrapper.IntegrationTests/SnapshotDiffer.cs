namespace SigilBuild.Wrapper.IntegrationTests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Win32;

/// <summary>
/// Snapshot of a directory subtree + an HKCU registry subtree, captured at a
/// single moment. Used by <see cref="WixClassInstallUninstallTests"/> to
/// compare pre-install state against post-uninstall state and assert that
/// uninstall is observation-clean (every observable mutation introduced by
/// install is reverted by uninstall).
/// </summary>
/// <param name="Files">Relative path (forward-slash form) -> SHA-256 hex of the file content.</param>
/// <param name="Directories">Relative paths (forward-slash form) of every subdirectory under the file root.</param>
/// <param name="RegistryValues">"<c>/sub/key/name</c>" -> string-rendering of the value. Subkey path uses forward slashes for parity with file paths.</param>
internal sealed record Snapshot(
    IReadOnlyDictionary<string, string> Files,
    IReadOnlyList<string> Directories,
    IReadOnlyDictionary<string, string> RegistryValues);

/// <summary>
/// Helpers for taking + diffing a <see cref="Snapshot"/>. Pure host-process
/// observation — no Windows Sandbox VM is involved, so the registry-side
/// reads use HKCU (not HKLM) to avoid requiring admin.
/// </summary>
internal static class SnapshotDiffer
{
    /// <summary>
    /// Capture a <see cref="Snapshot"/> of the given filesystem root and the
    /// registry subkey under HKCU. Both targets are tolerated to be absent —
    /// a missing path simply yields empty file/directory/registry collections,
    /// which is exactly the desired "pre-install baseline" shape.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static Snapshot Take(string fileRoot, string registrySubKey)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var dirs = new SortedSet<string>(StringComparer.Ordinal);
        if (Directory.Exists(fileRoot))
        {
            foreach (var d in Directory.EnumerateDirectories(fileRoot, "*", SearchOption.AllDirectories))
            {
                dirs.Add(Path.GetRelativePath(fileRoot, d).Replace('\\', '/'));
            }
            foreach (var f in Directory.EnumerateFiles(fileRoot, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(fileRoot, f).Replace('\\', '/');
                using var stream = File.OpenRead(f);
                var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                files[rel] = hash;
            }
        }

        var reg = new SortedDictionary<string, string>(StringComparer.Ordinal);
        using (var k = Registry.CurrentUser.OpenSubKey(registrySubKey))
        {
            if (k is not null)
            {
                Walk(k, string.Empty, reg);
            }
        }

        return new Snapshot(files, dirs.ToList(), reg);
    }

    [SupportedOSPlatform("windows")]
    private static void Walk(RegistryKey k, string prefix, SortedDictionary<string, string> reg)
    {
        foreach (var name in k.GetValueNames())
        {
            reg[$"{prefix}/{name}"] = k.GetValue(name)?.ToString() ?? string.Empty;
        }
        foreach (var sub in k.GetSubKeyNames())
        {
            using var s = k.OpenSubKey(sub);
            if (s is not null)
            {
                Walk(s, $"{prefix}/{sub}", reg);
            }
        }
    }

    /// <summary>
    /// Produce a human-readable list of differences between two snapshots.
    /// An empty list means the two snapshots are observably identical along
    /// the file-content + directory-presence + registry-value axes.
    /// </summary>
    public static IReadOnlyList<string> Diff(Snapshot a, Snapshot b)
    {
        var lines = new List<string>();
        foreach (var k in a.Files.Keys.Union(b.Files.Keys).OrderBy(s => s, StringComparer.Ordinal))
        {
            var va = a.Files.TryGetValue(k, out var x) ? x : null;
            var vb = b.Files.TryGetValue(k, out var y) ? y : null;
            if (va != vb)
            {
                lines.Add($"file {k}: {va ?? "<absent>"} -> {vb ?? "<absent>"}");
            }
        }
        foreach (var d in a.Directories.Union(b.Directories).OrderBy(s => s, StringComparer.Ordinal))
        {
            var inA = a.Directories.Contains(d);
            var inB = b.Directories.Contains(d);
            if (inA != inB)
            {
                lines.Add($"dir {d}: {(inA ? "present" : "absent")} -> {(inB ? "present" : "absent")}");
            }
        }
        foreach (var k in a.RegistryValues.Keys.Union(b.RegistryValues.Keys).OrderBy(s => s, StringComparer.Ordinal))
        {
            var va = a.RegistryValues.TryGetValue(k, out var x) ? x : null;
            var vb = b.RegistryValues.TryGetValue(k, out var y) ? y : null;
            if (va != vb)
            {
                lines.Add($"registry {k}: {va ?? "<absent>"} -> {vb ?? "<absent>"}");
            }
        }
        return lines;
    }
}
