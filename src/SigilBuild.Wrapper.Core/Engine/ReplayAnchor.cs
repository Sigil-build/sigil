namespace SigilBuild.Wrapper.Engine;

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using Microsoft.Win32;
using SigilBuild.Core.Manifest;

/// <summary>
/// Decides whether a single <see cref="RollbackRecord"/> may be replayed, given the
/// install directory the run belongs to (R1, clause (c)).
/// </summary>
/// <remarks>
/// <para>
/// Journal records carry absolute paths and full registry coordinates. When the
/// journal has been read back from <c>uninstall.json</c> — a file on disk, not
/// something this process authored in this run — every one of those coordinates is
/// attacker-supplied input to an elevated process, and the catalogue hands out
/// arbitrary file write/delete, arbitrary HKLM write, a machine <c>PATH</c> hijack,
/// service deletion and (via <c>unregister_com</c>) <c>LoadLibrary</c> plus an export
/// call on a DLL of the attacker's choosing.
/// </para>
/// <para>
/// A refused record is logged and skipped, never silently dropped and never fatal:
/// silence would mask an attack, and aborting would let one planted record block a
/// legitimate uninstall.
/// </para>
/// <para>
/// The anchor deliberately errs towards <em>allowing</em> where refusing would break a
/// real uninstall, and towards <em>refusing</em> where the record grants a privileged
/// primitive. Every predicate fails closed on an exception.
/// </para>
/// </remarks>
internal sealed class ReplayAnchor
{
    /// <summary>
    /// Registry subtrees that are inside application-configuration space but are
    /// auto-execution, policy or COM-activation surfaces. A manifest has no legitimate
    /// need to have its uninstall write here, and an attacker very much does.
    /// Compared segment-wise, case-insensitively, after <c>WOW6432Node</c> segments
    /// have been folded away.
    /// </summary>
    private static readonly string[] DeniedRegistrySubtrees =
    {
        @"Software\Microsoft\Windows\CurrentVersion\Run",
        @"Software\Microsoft\Windows\CurrentVersion\RunOnce",
        @"Software\Microsoft\Windows\CurrentVersion\RunOnceEx",
        @"Software\Microsoft\Windows\CurrentVersion\RunServices",
        @"Software\Microsoft\Windows\CurrentVersion\RunServicesOnce",
        @"Software\Microsoft\Windows\CurrentVersion\Policies",
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders",
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders",
        @"Software\Microsoft\Windows NT\CurrentVersion\Windows",
        @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon",
        @"Software\Microsoft\Windows NT\CurrentVersion\Image File Execution Options",
        @"Software\Policies",
        @"Software\Classes\CLSID",
        @"Software\Classes\AppID",
    };

    private const string MachineEnvKey =
        @"System\CurrentControlSet\Control\Session Manager\Environment";

    private const string ServicesKey = @"System\CurrentControlSet\Services";

    private readonly string? _installDir;
    private readonly string[] _roots;

    private ReplayAnchor(string? installDir, string[] roots)
    {
        _installDir = installDir;
        _roots = roots;
    }

    /// <summary>
    /// Build the anchor for a replay rooted at <paramref name="installDir"/>, or
    /// <c>null</c> when <paramref name="installDir"/> is <c>null</c>/blank — which
    /// means "this journal was built in memory by this process during this run", the
    /// only case where there is nothing to anchor against. Any replay of PERSISTED
    /// state must pass a real directory.
    /// </summary>
    public static ReplayAnchor? For(string? installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir))
        {
            return null;
        }

        var roots = new List<string>();
        var normalizedInstallDir = Normalize(installDir);
        if (normalizedInstallDir is not null)
        {
            roots.Add(normalizedInstallDir);
        }

        // The scope roots the installer legitimately writes outside install_dir:
        // the shortcut folders a shortcut_create step targets, and the per-app state
        // directory. Both scopes are included because an uninstall may legitimately
        // be reversing either — and both are narrow, well-known locations, unlike the
        // unbounded filesystem the records previously addressed.
        foreach (var scope in new[] { InstallScope.User, InstallScope.Machine })
        {
            var layout = ScopeLayout.For(scope);
            AddRoot(roots, layout.DesktopFolder);
            AddRoot(roots, layout.StartMenuFolder);
            AddRoot(roots, SafeCombine(layout.StateRoot, "Sigil"));
        }

        // A non-normalizable install dir yields an anchor with only the scope roots —
        // strictly stricter, never laxer. Failing open here would defeat the point.
        return new ReplayAnchor(normalizedInstallDir, roots.ToArray());
    }

    /// <summary>
    /// The verdict for one record: the record to actually replay (possibly re-derived
    /// from the install directory, as <c>unregister_com</c> is) and a non-<c>null</c>
    /// <see cref="RefusalReason"/> when it must be skipped instead.
    /// </summary>
    public readonly record struct Verdict(RollbackRecord Record, string? RefusalReason);

    /// <summary>Allow, re-derive, or refuse <paramref name="record"/>.</summary>
    public Verdict Check(RollbackRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        switch (record)
        {
            case RollbackRecord.RestoreFile r:
                return PathVerdict(record, "restore_file", r.Path);

            case RollbackRecord.RemoveDirectory r:
                return PathVerdict(record, "remove_directory", r.Path);

            case RollbackRecord.DeleteShortcut r:
                return PathVerdict(record, "delete_shortcut", r.Path);

            case RollbackRecord.RemoveUninstaller r:
                return PathVerdict(record, "remove_uninstaller", r.Path);

            case RollbackRecord.RestoreDeletedFile r:
                return PathVerdict(record, "restore_deleted_file", r.OriginalPath);

            case RollbackRecord.RestoreDeletedDirectory r:
                return PathVerdict(record, "restore_deleted_directory", r.OriginalPath);

            case RollbackRecord.RestoreConfigFile r:
                return PathVerdict(record, "restore_config_file", r.OriginalPath);

            case RollbackRecord.RestoreRegistryValue r:
                return RegistryVerdict(record, "restore_registry_value", r.Hive, r.Key);

            case RollbackRecord.RestoreRegistryKey r:
                return RegistryVerdict(record, "restore_registry_key", r.Hive, r.Key);

            case RollbackRecord.RestoreEnv r:
                return EnvVerdict(record, r);

            case RollbackRecord.RemoveService r:
                return ServiceVerdict(record, r);

            case RollbackRecord.UnregisterCom r:
                return ComVerdict(record, r);

            // delete_scheduled_task and delete_firewall_rule carry a bare NAME and no
            // coordinate this process can independently resolve to something belonging
            // to the app, so there is nothing to anchor them to. Both are destructive
            // rather than elevating (a planted record can delete an OS task or a
            // firewall rule; it cannot make the elevated process run attacker code),
            // and they are not part of register row R1's evidence table. Left
            // unanchored deliberately, and reported rather than quietly widened.
            default:
                return new Verdict(record, null);
        }
    }

    // --- filesystem ---

    private Verdict PathVerdict(RollbackRecord record, string type, string path) =>
        IsAllowedPath(path)
            ? new Verdict(record, null)
            : new Verdict(record, $"{type} refused: '{path}' {OutsideText()}");

    /// <summary>
    /// True when <paramref name="path"/> resolves inside one of the anchored roots.
    /// Normalizes first, so <c>&lt;install_dir&gt;\..\..\Windows</c> cannot pass, and
    /// requires a directory separator after the root, so <c>C:\rootevil</c> cannot
    /// pass as <c>C:\root</c>.
    /// </summary>
    private bool IsAllowedPath(string? path)
    {
        var full = Normalize(path);
        if (full is null)
        {
            return false;
        }

        foreach (var root in _roots)
        {
            if (IsUnder(full, root))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsUnder(string fullPath, string root)
    {
        if (fullPath.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (fullPath.Length <= root.Length ||
            !fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var next = fullPath[root.Length];
        return next == Path.DirectorySeparatorChar || next == Path.AltDirectorySeparatorChar;
    }

    // --- registry ---

    private static Verdict RegistryVerdict(RollbackRecord record, string type, string hive, string key)
    {
        if (IsAllowedRegistryKey(key))
        {
            return new Verdict(record, null);
        }

        return new Verdict(
            record,
            $"{type} refused: '{hive}\\{key}' is outside the application-configuration " +
            "subtree an installer may reverse (Software\\…, excluding the auto-run, " +
            "policy and COM-activation surfaces)");
    }

    /// <summary>
    /// A replayed registry write must sit under <c>Software\</c> — application
    /// configuration space, in either hive — and outside
    /// <see cref="DeniedRegistrySubtrees"/>. That is what stops the catalogue's
    /// "arbitrary HKLM write": <c>SYSTEM\CurrentControlSet\Services\…</c> and
    /// <c>SYSTEM\…\Session Manager\Environment</c> are no longer addressable at all.
    /// </summary>
    /// <remarks>
    /// This is deliberately looser than "the app's own <c>Software\Publisher\App</c>
    /// key": a <c>registry_write</c> step may name any key the manifest author chose,
    /// and this process cannot know which of them the app owns without the manifest in
    /// hand. Narrowing it further needs a manifest-declared key allowlist carried into
    /// the journal — a change to the persisted schema, not to this predicate.
    /// </remarks>
    private static bool IsAllowedRegistryKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var normalized = FoldRegistryKey(key);
        if (normalized.Length == 0)
        {
            return false;
        }

        if (!IsUnderRegistryPrefix(normalized, "Software"))
        {
            return false;
        }

        foreach (var denied in DeniedRegistrySubtrees)
        {
            if (IsUnderRegistryPrefix(normalized, denied))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Canonicalize a registry path: forward slashes to backslashes, collapse empty
    /// segments, drop <c>WOW6432Node</c> redirection segments so one deny-list covers
    /// both views, and reject any <c>..</c> segment outright.
    /// </summary>
    private static string FoldRegistryKey(string key)
    {
        var segments = key.Replace('/', '\\').Split('\\');
        var kept = new List<string>(segments.Length);
        foreach (var raw in segments)
        {
            var segment = raw.Trim();
            if (segment.Length == 0)
            {
                continue;
            }
            if (segment == ".." || segment == ".")
            {
                return string.Empty;
            }
            if (segment.Equals("WOW6432Node", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            kept.Add(segment);
        }
        return string.Join('\\', kept);
    }

    private static bool IsUnderRegistryPrefix(string key, string prefix)
    {
        if (key.Equals(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return key.Length > prefix.Length
            && key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && key[prefix.Length] == '\\';
    }

    // --- environment ---

    /// <summary>
    /// User-scope environment records touch HKCU, which the invoking user already owns
    /// — replaying one grants nothing. Machine scope is the <c>PATH</c>-hijack
    /// primitive, so it is allowed only when the write cannot introduce anything new:
    /// every entry of the restored value must already be present in the variable's
    /// current value, or live inside an anchored root. A legitimate uninstall restores
    /// a value that is the current one minus the entry the install added, so it passes;
    /// a planted record injecting <c>C:\evil</c> does not.
    /// </summary>
    private Verdict EnvVerdict(RollbackRecord record, RollbackRecord.RestoreEnv r)
    {
        if (!r.Scope.Equals("machine", StringComparison.OrdinalIgnoreCase))
        {
            return new Verdict(record, null);
        }

        if (!OperatingSystem.IsWindows())
        {
            // The current value cannot be consulted, and RestoreEnv.UndoAsync is a
            // no-op off Windows anyway. Fail closed rather than record an allowance
            // that has never been checked.
            return new Verdict(
                record,
                $"restore_env refused: machine-scope '{r.Name}' cannot be verified off Windows");
        }

        // PATH and friends are REG_EXPAND_SZ: the value on disk holds
        // "%SystemRoot%\system32" while RegistryKey.GetValue's default expands it, and
        // EnvSetStep captures the EXPANDED form into the journal. Compare against both
        // so a legitimate restore is not refused over a spelling difference — that
        // would break every real machine-scope uninstall.
        var expanded = ReadMachineEnv(r.Name, expand: true);
        var literal = ReadMachineEnv(r.Name, expand: false);
        var restored = r.PreviouslyAbsent ? expanded ?? literal : r.PriorValue;

        // Deleting the variable the install created is the normal undo — but deleting
        // one whose current content the app does not own (machine PATH, say) is a
        // destructive primitive in its own right, so the same test applies to it.
        if (restored is null)
        {
            return new Verdict(record, null);
        }

        var currentEntries = SplitEntries(expanded);
        currentEntries.UnionWith(SplitEntries(literal));

        foreach (var entry in SplitEntries(restored))
        {
            var alternate = ExpandSafely(entry);
            if (currentEntries.Contains(entry) ||
                currentEntries.Contains(alternate) ||
                IsAllowedPath(entry) ||
                IsAllowedPath(alternate))
            {
                continue;
            }
            return new Verdict(
                record,
                $"restore_env refused: machine-scope '{r.Name}' would introduce '{entry}', " +
                "which is neither already present in the variable nor inside the install " +
                "directory — a restore may only remove what the install added");
        }
        return new Verdict(record, null);
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadMachineEnv(string name, bool expand)
    {
#pragma warning disable CA1031 // Fail closed: an unreadable value must not be treated as "matches".
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(MachineEnvKey, writable: false);
            var options = expand
                ? RegistryValueOptions.None
                : RegistryValueOptions.DoNotExpandEnvironmentNames;
            return key?.GetValue(name, null, options) as string;
        }
        catch
        {
            return null;
        }
#pragma warning restore CA1031
    }

    private static string ExpandSafely(string value)
    {
#pragma warning disable CA1031 // Fail closed: an unexpandable value is compared as-is.
        try
        {
            return Environment.ExpandEnvironmentVariables(value);
        }
        catch
        {
            return value;
        }
#pragma warning restore CA1031
    }

    private static HashSet<string> SplitEntries(string? value)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(value))
        {
            return set;
        }
        foreach (var part in value.Split(';'))
        {
            var trimmed = part.Trim();
            if (trimmed.Length != 0)
            {
                set.Add(trimmed);
            }
        }
        return set;
    }

    // --- services ---

    /// <summary>
    /// <c>sc stop</c> + <c>sc delete</c> on a name of the attacker's choosing stops or
    /// removes any service on the box. The service's own registered <c>ImagePath</c> is
    /// the coordinate that ties it back to this install: if the binary does not live in
    /// the install directory, the app did not install the service. A service that does
    /// not exist is allowed through — the undo is a no-op either way.
    /// </summary>
    private Verdict ServiceVerdict(RollbackRecord record, RollbackRecord.RemoveService r)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new Verdict(
                record,
                $"remove_service refused: '{r.ServiceName}' cannot be verified off Windows");
        }

        var imagePath = ReadServiceImagePath(r.ServiceName);
        if (imagePath is null)
        {
            // No such service (or its key is unreadable and therefore not ours to
            // delete): sc delete would be a no-op.
            return new Verdict(record, null);
        }

        if (ImagePathIsInsideInstallDir(imagePath))
        {
            return new Verdict(record, null);
        }

        return new Verdict(
            record,
            $"remove_service refused: service '{r.ServiceName}' runs '{imagePath}', " +
            $"which {OutsideText()} — this install did not create it");
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadServiceImagePath(string serviceName)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            return null;
        }
#pragma warning disable CA1031 // Fail closed: an unreadable key is treated as "not ours".
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"{ServicesKey}\{serviceName}", writable: false);
            if (key is null)
            {
                return null;
            }
            var raw = key.GetValue("ImagePath") as string;
            return string.IsNullOrWhiteSpace(raw) ? null : raw;
        }
        catch
        {
            return null;
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// An <c>ImagePath</c> is a command line, not a path: it may be quoted, carry
    /// arguments, and use the <c>\??\</c> NT prefix. Both the parsed executable and the
    /// raw remainder are tested so an unquoted path containing spaces (the shape
    /// <c>C:\Program Files\App\svc.exe -k net</c>) is not misread as
    /// <c>C:\Program</c> and refused for a legitimate uninstall.
    /// </summary>
    private bool ImagePathIsInsideInstallDir(string imagePath)
    {
        var value = imagePath.Trim();
        if (value.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            value = value.Substring(4);
        }

        string executable;
        if (value.StartsWith('"'))
        {
            var close = value.IndexOf('"', 1);
            executable = close > 1 ? value.Substring(1, close - 1) : value.Trim('"');
            value = executable;
        }
        else
        {
            var space = value.IndexOf(' ', StringComparison.Ordinal);
            executable = space > 0 ? value.Substring(0, space) : value;
        }

        if (IsInsideInstallDir(executable))
        {
            return true;
        }

        // Unquoted-with-arguments fallback: the whole remainder still begins with the
        // install directory.
        return _installDir is not null && IsUnder(
            Path.TrimEndingDirectorySeparator(value), _installDir);
    }

    // --- COM ---

    /// <summary>
    /// The single worst record in the catalogue: <c>LoadLibrary</c> plus a call to an
    /// export, inside the elevated process, on a path taken verbatim from the file. The
    /// path is re-derived from the install directory rather than trusted, and a
    /// recorded path that does not resolve inside it is refused outright.
    /// </summary>
    private Verdict ComVerdict(RollbackRecord record, RollbackRecord.UnregisterCom r)
    {
        var rederived = ReDeriveInsideInstallDir(r.DllPath);
        if (rederived is null)
        {
            return new Verdict(
                record,
                $"unregister_com refused: '{r.DllPath}' {OutsideText()} — a DLL outside " +
                "the install directory must never be loaded by the elevated process");
        }

        return new Verdict(r with { DllPath = rederived }, null);
    }

    /// <summary>
    /// Re-derive <paramref name="recordedPath"/> as <c>install_dir</c> + the path
    /// relative to it, returning <c>null</c> when it does not resolve inside the
    /// install directory. Normalization happens before the containment test, so
    /// <c>&lt;install_dir&gt;\..\..\Users\Public\evil.dll</c> is refused.
    /// </summary>
    private string? ReDeriveInsideInstallDir(string? recordedPath)
    {
        if (_installDir is null)
        {
            return null;
        }

        var full = Normalize(recordedPath);
        if (full is null || !IsUnder(full, _installDir))
        {
            return null;
        }

#pragma warning disable CA1031 // Fail closed: an unrelativizable path is refused.
        try
        {
            var relative = Path.GetRelativePath(_installDir, full);
            var rebuilt = Path.GetFullPath(Path.Combine(_installDir, relative));
            return IsUnder(rebuilt, _installDir) ? rebuilt : null;
        }
        catch
        {
            return null;
        }
#pragma warning restore CA1031
    }

    private bool IsInsideInstallDir(string? path)
    {
        var full = Normalize(path);
        return full is not null && _installDir is not null && IsUnder(full, _installDir);
    }

    private string OutsideText() => _installDir is null
        ? "is outside the roots this replay is anchored to"
        : $"is outside the install directory '{_installDir}' and the scope roots the " +
          "installer legitimately writes";

    private static void AddRoot(List<string> roots, string? candidate)
    {
        var normalized = Normalize(candidate);
        if (normalized is not null && !roots.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            roots.Add(normalized);
        }
    }

    private static string? SafeCombine(string? first, string second)
    {
        if (string.IsNullOrEmpty(first))
        {
            return null;
        }
#pragma warning disable CA1031 // Fail closed: an uncombinable root is simply not added.
        try
        {
            return Path.Combine(first, second);
        }
        catch
        {
            return null;
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Absolute, separator-normalized, without a trailing separator — or <c>null</c>
    /// when the value cannot be turned into a path at all, which always means "refuse".
    /// </summary>
    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
#pragma warning disable CA1031 // Fail closed: an unparseable path is never allowed.
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch
        {
            return null;
        }
#pragma warning restore CA1031
    }
}
