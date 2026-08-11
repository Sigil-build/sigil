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
    /// Registry subtrees that sit inside application-configuration space but are
    /// auto-execution, policy or COM-activation surfaces, and that no manifest has a
    /// legitimate reason to write at all. Refused outright, in both directions — a
    /// planted record must not be able to write them, and deleting one is destructive.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This list is deliberately NOT the defence against execution hijacking. Rounds 0,
    /// 1 and 2 of review each turned up another progid whose <c>shell\…\command</c>
    /// grants machine-wide execution (<c>exefile</c>, then <c>Directory</c>, then
    /// <c>txtfile</c> / <c>lnkfile</c> / <c>mscfile</c>), which is what enumerating
    /// names buys you. The shape is what is dangerous, so
    /// <see cref="IsExecutionShapedKey"/> denies the shape and this list is reduced to
    /// the coordinates that are categorically not application configuration.
    /// </para>
    /// <para>
    /// Compared segment-wise, case-insensitively, after <c>WOW6432Node</c> segments have
    /// been folded away and after <c>HKCR</c> has been rewritten to its
    /// <c>Software\Classes</c> equivalent.
    /// </para>
    /// </remarks>
    private static readonly string[] DeniedRegistrySubtrees =
    {
        // Auto-run, policy and shell-integration.
        @"Software\Microsoft\Windows\CurrentVersion\Run",
        @"Software\Microsoft\Windows\CurrentVersion\RunOnce",
        @"Software\Microsoft\Windows\CurrentVersion\RunOnceEx",
        @"Software\Microsoft\Windows\CurrentVersion\RunServices",
        @"Software\Microsoft\Windows\CurrentVersion\RunServicesOnce",
        @"Software\Microsoft\Windows\CurrentVersion\Policies",
        @"Software\Microsoft\Windows\CurrentVersion\App Paths",
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders",
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders",
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\Browser Helper Objects",
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\ShellServiceObjectDelayLoad",
        @"Software\Microsoft\Windows\CurrentVersion\Shell Extensions",
        @"Software\Microsoft\Windows NT\CurrentVersion\Windows",
        @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon",
        @"Software\Microsoft\Windows NT\CurrentVersion\Image File Execution Options",
        @"Software\Microsoft\Active Setup\Installed Components",
        @"Software\Microsoft\Command Processor",
        @"Software\Policies",

        // COM activation.
        @"Software\Classes\CLSID",
        @"Software\Classes\AppID",
        @"Software\Classes\Interface",
        @"Software\Classes\TypeLib",
        @"Software\Classes\Protocols",

        // OS-owned pseudo-classes: not an application's own progid under any reading,
        // and their whole subtree governs how Windows treats every file or folder.
        @"Software\Classes\*",
        @"Software\Classes\Unknown",
        @"Software\Classes\Directory",
        @"Software\Classes\Folder",
        @"Software\Classes\Drive",
        @"Software\Classes\AllFilesystemObjects",
    };

    /// <summary>
    /// The leaf of a shell-verb definition. Dangerous only when the key is actually
    /// defining a verb — i.e. some earlier segment is <c>shell</c>.
    /// </summary>
    private static readonly string[] ShellVerbLeaves = { "command", "ddeexec" };

    /// <summary>
    /// Class-registration segments that name a module for Windows to load. Dangerous
    /// only <em>under</em> <c>Software\Classes</c>, which is where they mean something.
    /// </summary>
    private static readonly string[] ClassRegistrationSegments =
    {
        "shellex",
        "InprocServer32", "InprocServer", "LocalServer32", "LocalServer",
        "InprocHandler32", "InprocHandler", "TreatAs",
    };

    /// <summary>
    /// Multimedia / driver mapping keys, which live at a fixed place in the hive.
    /// </summary>
    private static readonly string[] DriverMappingSegments =
    {
        "Drivers32", "Drivers.desc", "MCI32", "MCI Extensions",
    };

    private const string ClassesPrefix = @"Software\Classes";

    private const string WindowsNtCurrentVersionPrefix =
        @"Software\Microsoft\Windows NT\CurrentVersion";

    private const string MachineEnvKey =
        @"System\CurrentControlSet\Control\Session Manager\Environment";

    private const string UserEnvKey = "Environment";

    private const string ServicesKey = @"System\CurrentControlSet\Services";

    /// <summary>
    /// Sub-paths that are excised from an otherwise-allowed filesystem root. The Start
    /// Menu is allowed because <c>shortcut_create</c> writes there and its reversal must
    /// replay — but the Start Menu's own subtree contains <c>Programs\Startup</c>, a
    /// per-logon execution surface. Sigil never places a shortcut there
    /// (<c>ShortcutCreateStep.ResolveLocation</c> resolves only <c>start_menu</c> and
    /// <c>desktop</c> to the folder itself), so excising it costs no legitimate replay.
    /// </summary>
    private static readonly string[] ExcludedRootSubPaths =
    {
        @"Programs\Startup",
        "Startup",
    };

    private readonly string _installDir;
    private readonly string[] _fileRoots;
    private readonly string[] _excludedRoots;

    private ReplayAnchor(string installDir, string[] fileRoots, string[] excludedRoots)
    {
        _installDir = installDir;
        _fileRoots = fileRoots;
        _excludedRoots = excludedRoots;
    }

    /// <summary>
    /// Build the anchor for <paramref name="anchorage"/>, or <c>null</c> when the
    /// caller declared <see cref="ReplayAnchorage.InProcess"/>.
    /// </summary>
    public static ReplayAnchor? For(ReplayAnchorage anchorage)
    {
        ArgumentNullException.ThrowIfNull(anchorage);
        if (!anchorage.IsAnchored)
        {
            return null;
        }

        // A non-normalizable install dir yields an anchor whose install root is a
        // sentinel nothing can be under — strictly stricter, never laxer. Failing open
        // here would defeat the point.
        var installDir = Normalize(anchorage.InstallDir)
            ?? "\u0000:\\unresolvable-install-dir";

        var roots = new List<string> { installDir };
        var excluded = new List<string>();

        // The scope roots the installer legitimately writes OUTSIDE install_dir: the
        // shortcut folders a shortcut_create step targets and THIS app's own state
        // directory. FILESYSTEM records only — these are user-writable locations and
        // must never be reused as an allowlist for a privileged primitive such as the
        // machine PATH (see OwnedByThisInstall).
        //
        // Only the scope being replayed is allowed when the caller knows it; a machine
        // uninstall has no business deleting a per-user shortcut and vice versa.
        var scopes = anchorage.Scope is { } only
            ? new[] { only }
            : new[] { InstallScope.User, InstallScope.Machine };

        foreach (var scope in scopes)
        {
            var layout = ScopeLayout.For(scope);
            AddRoot(roots, layout.DesktopFolder);
            AddRoot(roots, layout.StartMenuFolder);

            // The per-app state directory, never the shared <StateRoot>\Sigil parent:
            // allowing the parent lets one app's journal delete or overwrite another
            // app's uninstall.json, and in machine scope the rewritten file would come
            // out Administrators-owned and pass the victim's provenance gate on its next
            // load — attacker content laundered into trusted state. With no app id the
            // state directory is simply not allowed at all.
            if (anchorage.AppId is { } appId)
            {
                AddRoot(roots, SafeCombine(SafeCombine(layout.StateRoot, "Sigil"), appId));
            }

            // Startup is inside the Start Menu subtree and is a per-logon execution
            // surface; nothing Sigil writes lands there.
            foreach (var excludedSubPath in ExcludedRootSubPaths)
            {
                AddRoot(excluded, SafeCombine(layout.StartMenuFolder, excludedSubPath));
            }
        }

        return new ReplayAnchor(installDir, roots.ToArray(), excluded.ToArray());
    }

    /// <summary>
    /// The verdict for one record: the record to actually replay (possibly re-derived
    /// from the install directory, as <c>unregister_com</c> is) and a non-<c>null</c>
    /// <see cref="Refusal"/> when it must be skipped instead.
    /// </summary>
    public readonly record struct Verdict(RollbackRecord Record, RefusedRecord? Refusal)
    {
        /// <summary>The operator-facing line, or <c>null</c> when the record is allowed.</summary>
        public string? RefusalMessage => Refusal?.Message;
    }

    /// <summary>Allow <paramref name="record"/> to replay unchanged.</summary>
    private static Verdict Allow(RollbackRecord record) => new(record, null);

    /// <summary>Skip <paramref name="record"/>, capturing the structured reason (R15).</summary>
    private static Verdict Refuse(
        RollbackRecord record,
        string recordType,
        string target,
        ReplayRefusalCode code,
        string message) =>
        new(record, new RefusedRecord(recordType, target, code, message));

    /// <summary>Allow, re-derive, or refuse <paramref name="record"/>.</summary>
    public Verdict Check(RollbackRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        switch (record)
        {
            // The four content-bearing records are checked on BOTH sides. The destination
            // says where bytes land; the source says whose bytes they are, and the
            // register's own wording for these rows is "arbitrary file / tree write FROM
            // AN ATTACKER-CHOSEN STASH". A contained destination fed from an uncontained
            // source is still an attacker-content write.
            case RollbackRecord.RestoreFile r:
                return PathVerdict(record, "restore_file", r.Path, r.BackupPath);

            case RollbackRecord.RemoveDirectory r:
                return PathVerdict(record, "remove_directory", r.Path);

            case RollbackRecord.DeleteShortcut r:
                return PathVerdict(record, "delete_shortcut", r.Path);

            case RollbackRecord.RemoveUninstaller r:
                return PathVerdict(record, "remove_uninstaller", r.Path);

            case RollbackRecord.RestoreDeletedFile r:
                return PathVerdict(record, "restore_deleted_file", r.OriginalPath, r.StashPath);

            case RollbackRecord.RestoreDeletedDirectory r:
                return PathVerdict(record, "restore_deleted_directory", r.OriginalPath, r.StashPath);

            case RollbackRecord.RestoreConfigFile r:
                return PathVerdict(record, "restore_config_file", r.OriginalPath, r.StashPath);

            case RollbackRecord.RestoreRegistryValue r:
                // PreviouslyAbsent → the undo deletes the value it wrote; nothing of the
                // record's choosing is written, so there is no command line to contain.
                return RegistryVerdict(
                    record,
                    "restore_registry_value",
                    r.Hive,
                    r.Key,
                    r.PreviouslyAbsent ? Array.Empty<object?>() : new[] { r.PriorValue });

            case RollbackRecord.RestoreRegistryKey r:
                // PreviouslyAbsent → RestoreRegistryKey.UndoAsync returns immediately.
                return RegistryVerdict(
                    record,
                    "restore_registry_key",
                    r.Hive,
                    r.Key,
                    r.PreviouslyAbsent ? Array.Empty<object?>() : ValuesOf(r.ValuesAtKeyLevel));

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
                return Allow(record);
        }
    }

    // --- filesystem ---

    /// <summary>
    /// Check a record's destination and, where it has one, the source its bytes come
    /// from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <c>null</c> <paramref name="contentSource"/> means the record writes nothing of
    /// its own choosing (a delete, or a "the file did not exist before" restore), so
    /// there is nothing to constrain.
    /// </para>
    /// <para>
    /// Requiring the source to be anchored costs no legitimate replay. A
    /// <c>restore_file</c> backup is written as <c>&lt;destination&gt;.sigil-bak</c>
    /// (<c>FileCopyStep</c>), so it is inside the anchor exactly when its destination is.
    /// The <c>%TEMP%\sigil-fd-*</c> / <c>-dd-*</c> / <c>-cfg-*</c> stashes belong to the
    /// mid-install rollback only: <c>RollbackJournal.DiscardTransientStashes</c> reclaims
    /// them the moment the install commits, and the journal is persisted after that — so
    /// a stash named by a record that reached an ANCHORED replay is either already gone
    /// (the undo is a no-op) or was planted. Mid-install rollback runs
    /// <see cref="ReplayAnchorage.InProcess"/> and is unaffected.
    /// </para>
    /// </remarks>
    private Verdict PathVerdict(
        RollbackRecord record, string type, string path, string? contentSource = null)
    {
        if (!IsAllowedPath(path))
        {
            return Refuse(
                record,
                type,
                path,
                ReplayRefusalCode.PathOutsideInstallRoots,
                $"{type} refused: '{path}' {OutsideText()}");
        }

        if (contentSource is not null && !IsAllowedPath(contentSource))
        {
            return Refuse(
                record,
                type,
                contentSource,
                ReplayRefusalCode.ContentSourceOutsideInstallRoots,
                $"{type} refused: its destination '{path}' is in range but the content it " +
                $"would restore comes from '{contentSource}', which {OutsideText()} — a " +
                "contained destination fed from an uncontained source is still an " +
                "attacker-content write");
        }

        return Allow(record);
    }

    /// <summary>
    /// True when <paramref name="path"/> resolves inside one of the anchored
    /// filesystem roots. Normalizes first, so <c>&lt;install_dir&gt;\..\..\Windows</c>
    /// cannot pass, and requires a directory separator after the root, so
    /// <c>C:\rootevil</c> cannot pass as <c>C:\root</c>.
    /// </summary>
    private bool IsAllowedPath(string? path)
    {
        var full = Normalize(path);
        if (full is null)
        {
            return false;
        }

        // Excisions win over roots: the Start Menu is allowed, its Startup subtree is not.
        foreach (var excludedRoot in _excludedRoots)
        {
            if (IsUnder(full, excludedRoot))
            {
                return false;
            }
        }

        foreach (var root in _fileRoots)
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

    private Verdict RegistryVerdict(
        RollbackRecord record,
        string type,
        string hive,
        string key,
        IReadOnlyList<object?> writtenValues)
    {
        var target = $"{hive}\\{key}";

        var effective = EffectiveRegistryPath(hive, key);
        if (effective is null || !IsAllowedRegistryKey(effective))
        {
            return Refuse(
                record,
                type,
                target,
                ReplayRefusalCode.RegistryOutsideApplicationSpace,
                $"{type} refused: '{target}' is outside the application-configuration " +
                "subtree an installer may reverse (HKLM/HKCU Software\\…, excluding the " +
                "auto-run, policy and COM-activation surfaces)");
        }

        // The key is ordinary application space — but if its SHAPE makes it an execution
        // mapping, whatever is written there is a command line Windows will later run.
        // An app registering its own file type legitimately writes
        // Software\Classes\Acme.Document\shell\open\command; it writes its OWN exe there.
        // Anything pointing somewhere this install does not own is a hijack whatever the
        // progid is called, which is why this tests the shape and the value rather than a
        // list of names.
        if (!IsExecutionShapedKey(effective))
        {
            return Allow(record);
        }

        // An execution mapping in a MACHINE hive is the same primitive as an entry on the
        // machine PATH — it names a binary the system will run for every user — so it is
        // held to the same standard: inside install_dir AND writable by administrators
        // only. Without the second half, an install directory an unprivileged user can
        // write (a /D= into %TEMP%, a recorded value that squeaks past the anchor floor)
        // would let a planted record point exefile\shell\open\command at an exe that user
        // controls. HKCU is judged as user scope, because a per-user install legitimately
        // lands in a user-writable directory and requiring otherwise would refuse every
        // per-user file association on uninstall.
        var isMachineHive = IsMachineHive(hive);

        foreach (var value in writtenValues)
        {
            if (value is null)
            {
                continue;
            }

            if (value is not string command)
            {
                return Refuse(
                    record,
                    type,
                    target,
                    ReplayRefusalCode.ExecutionMappingUncheckable,
                    $"{type} refused: '{target}' is an execution mapping and the value " +
                    "being restored is not a command line that can be checked");
            }

            if (!ProgramIsOwnedByThisInstall(command, isMachineHive))
            {
                return Refuse(
                    record,
                    type,
                    target,
                    ReplayRefusalCode.ExecutionMappingNotOwned,
                    $"{type} refused: '{target}' is an execution mapping and '{command}' " +
                    $"{(isMachineHive ? "is not a program inside the install directory " +
                        $"'{_installDir}' that only administrators can write" : OutsideText())}" +
                    " — restoring it would hand Windows a program this install does not own");
            }
        }

        return Allow(record);
    }

    /// <summary>
    /// Machine-visible hives. <c>HKCR</c> counts: writes through it land in
    /// <c>HKLM\Software\Classes</c>, so a value restored there affects every user.
    /// </summary>
    private static bool IsMachineHive(string? hive) => hive switch
    {
        "HKLM" or "HKEY_LOCAL_MACHINE" or "HKCR" or "HKEY_CLASSES_ROOT" => true,
        _ => false,
    };

    /// <summary>
    /// True when the program named by <paramref name="commandLine"/> is one this install
    /// owns — the same test <see cref="OwnedByThisInstall"/> applies to a
    /// <c>PATH</c> entry, applied to the executable a command line names.
    /// </summary>
    private bool ProgramIsOwnedByThisInstall(string commandLine, bool isMachine)
    {
        foreach (var candidate in ProgramPathCandidates(commandLine))
        {
            if (OwnedByThisInstall(candidate, isMachine))
            {
                return true;
            }
        }
        return false;
    }

    private static object?[] ValuesOf(
        IReadOnlyList<RegistryValueSnapshot>? snapshots)
    {
        if (snapshots is null || snapshots.Count == 0)
        {
            return Array.Empty<object?>();
        }
        var values = new object?[snapshots.Count];
        for (var i = 0; i < snapshots.Count; i++)
        {
            values[i] = snapshots[i].Value;
        }
        return values;
    }

    /// <summary>
    /// True when the key's SHAPE makes it an execution mapping — a
    /// <c>shell\&lt;verb&gt;\command</c>, a <c>shellex</c> handler, a COM server path, or a
    /// multimedia driver mapping — regardless of which progid or component it belongs to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deny the shape, not the instances. Enumerating progids does not converge:
    /// <c>exefile</c>, <c>Directory</c>, <c>txtfile</c>, <c>lnkfile</c> and <c>mscfile</c>
    /// all give machine-wide execution and each was found in a different review round.
    /// Any progid — including one Windows ships that this code has never heard of — is
    /// covered here, while an app restoring its own verb to its own binary still passes.
    /// </para>
    /// <para>
    /// The match is anchored to the STRUCTURAL POSITION, not to the word. <c>command</c>
    /// counts only when the key is actually defining a shell verb (some earlier segment
    /// is <c>shell</c>), which catches <c>&lt;progid&gt;\shell\&lt;verb&gt;\command</c> and
    /// <c>…\Explorer\CommandStore\shell\&lt;verb&gt;\command</c> alike; the
    /// class-registration segments count only under <c>Software\Classes</c>; the driver
    /// maps only under <c>Software\Microsoft\Windows NT\CurrentVersion</c>. A plain
    /// application key that happens to contain a segment called <c>command</c>,
    /// <c>LocalServer</c> or <c>MCI32</c> — <c>Software\Acme\App\command</c> — carries no
    /// execution semantics and must replay normally, or a legitimate uninstall leaves a
    /// stale value behind and pollutes the refusal list S5 reads for R15.
    /// </para>
    /// </remarks>
    private static bool IsExecutionShapedKey(string normalizedKey)
    {
        var segments = normalizedKey.Split('\\');

        // shell\<verb>\command | ...\ddeexec — a verb definition, wherever it lives.
        for (var i = 0; i < segments.Length; i++)
        {
            if (!MatchesAny(segments[i], ShellVerbLeaves))
            {
                continue;
            }
            for (var j = 0; j < i; j++)
            {
                if (segments[j].Equals("shell", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        if (IsUnderRegistryPrefix(normalizedKey, ClassesPrefix) &&
            AnySegmentMatches(segments, ClassRegistrationSegments))
        {
            return true;
        }

        return IsUnderRegistryPrefix(normalizedKey, WindowsNtCurrentVersionPrefix)
            && AnySegmentMatches(segments, DriverMappingSegments);
    }

    private static bool AnySegmentMatches(string[] segments, string[] markers)
    {
        foreach (var segment in segments)
        {
            if (MatchesAny(segment, markers))
            {
                return true;
            }
        }
        return false;
    }

    private static bool MatchesAny(string segment, string[] markers)
    {
        foreach (var marker in markers)
        {
            if (segment.Equals(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Fold a (hive, key) pair into the single canonical path the rules are written
    /// against, or <c>null</c> when the hive itself is out of bounds.
    /// </summary>
    /// <remarks>
    /// The hive is load-bearing. <c>HKLM</c> and <c>HKCU</c> are the two an installer
    /// writes; <c>HKCR</c> is the merged view of <c>HKLM\Software\Classes</c> and
    /// <c>HKCU\Software\Classes</c>, so it is rewritten into that form and judged by
    /// the same rules — otherwise <c>HKCR\exefile\shell\open\command</c> would sidestep
    /// the <c>Software\</c> prefix test entirely. <c>HKU</c>, <c>HKCC</c> and anything
    /// unrecognized are refused outright: an installer's rollback has no business in
    /// another user's hive or in the hardware profile.
    /// </remarks>
    private static string? EffectiveRegistryPath(string? hive, string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        return hive switch
        {
            "HKLM" or "HKEY_LOCAL_MACHINE" or "HKCU" or "HKEY_CURRENT_USER" => FoldRegistryKey(key),
            "HKCR" or "HKEY_CLASSES_ROOT" => FoldRegistryKey(@"Software\Classes\" + key),
            _ => null,
        };
    }

    /// <summary>
    /// A replayed registry write must sit under <c>Software\</c> — application
    /// configuration space — and outside <see cref="DeniedRegistrySubtrees"/>. That is
    /// what stops the catalogue's "arbitrary HKLM write":
    /// <c>SYSTEM\CurrentControlSet\Services\…</c> and
    /// <c>SYSTEM\…\Session Manager\Environment</c> are no longer addressable at all,
    /// and neither are the execution-hijack surfaces that do live under
    /// <c>Software\</c>.
    /// </summary>
    /// <remarks>
    /// This is deliberately looser than "the app's own <c>Software\Publisher\App</c>
    /// key": a <c>registry_write</c> step may name any key the manifest author chose,
    /// and this process cannot know which of them the app owns without the manifest in
    /// hand. Narrowing it to a true allowlist needs a manifest-declared key list
    /// carried into the persisted journal — a wire-schema change, escalated separately,
    /// not a change to this predicate.
    /// </remarks>
    private static bool IsAllowedRegistryKey(string normalizedKey)
    {
        if (normalizedKey.Length == 0)
        {
            return false;
        }

        if (!IsUnderRegistryPrefix(normalizedKey, "Software"))
        {
            return false;
        }

        foreach (var denied in DeniedRegistrySubtrees)
        {
            if (IsUnderRegistryPrefix(normalizedKey, denied))
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
    /// A <c>restore_env</c> replay writes an attacker-chosen string into an environment
    /// variable. Machine scope is R1's named <c>PATH</c>-hijack primitive; user scope is
    /// no safer during an ELEVATED replay, because HKCU is then the administrator's own
    /// hive and a standard user planting the record would be hijacking it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both scopes therefore obey the same shape: a restore may only <em>remove</em>.
    /// Every entry of the value being written must either already be present in the
    /// variable's current value — which is what a genuine "put back what was there
    /// before the install appended to it" restore looks like — or be a directory this
    /// install owns.
    /// </para>
    /// <para>
    /// "Owns" is deliberately narrower than the filesystem allowlist: the install
    /// directory only, never the shortcut folders or the per-app state directory. Those
    /// are user-writable, and treating a user-writable directory as an acceptable new
    /// machine-<c>PATH</c> entry would leave the hijack primitive fully alive. For
    /// machine scope the directory must additionally pass
    /// <see cref="StateDirectorySecurity.IsAdminOnlyWritable"/>, so a machine install
    /// redirected into a user-writable location cannot re-introduce it either.
    /// </para>
    /// </remarks>
    private Verdict EnvVerdict(RollbackRecord record, RollbackRecord.RestoreEnv r)
    {
        var isMachine = r.Scope.Equals("machine", StringComparison.OrdinalIgnoreCase);
        var target = $"env:{r.Scope}:{r.Name}";

        if (!OperatingSystem.IsWindows())
        {
            // The current value cannot be consulted, and RestoreEnv.UndoAsync is a
            // no-op off Windows anyway. Fail closed rather than record an allowance
            // that has never been checked.
            return Refuse(
                record,
                "restore_env",
                target,
                ReplayRefusalCode.EnvironmentUnverifiable,
                $"restore_env refused: '{r.Name}' cannot be verified off Windows");
        }

        // PATH and friends are REG_EXPAND_SZ: the value on disk holds
        // "%SystemRoot%\system32" while RegistryKey.GetValue's default expands it, and
        // EnvSetStep captures the EXPANDED form into the journal. Compare against both
        // so a legitimate restore is not refused over a spelling difference — that
        // would break every real machine-scope uninstall.
        var expanded = ReadEnv(isMachine, r.Name, expand: true);
        var literal = ReadEnv(isMachine, r.Name, expand: false);

        if (r.PreviouslyAbsent)
        {
            // The undo DELETES the variable, on the record's claim that the install
            // created it. For an ordinary application variable that claim is cheap to
            // honour and expensive to refuse: an installer legitimately sets things like
            // ACME_HOME to a data directory outside install_dir, and refusing to remove
            // it would leave a stale variable behind after every real uninstall. A false
            // claim there costs one deleted application variable — a nuisance, not a
            // privilege.
            //
            // For a variable the OS itself depends on, deleting it is a destructive
            // primitive in its own right: "restore machine PATH to absent" would break
            // the box. Those may only be removed if this install owns every entry in
            // them, which in practice means never for PATH.
            //
            // Note the ownership test cannot be expressed by comparing the current value
            // against itself: that tautology would make this branch incapable of ever
            // refusing anything.
            if (!IsSystemCriticalVariable(r.Name))
            {
                return Allow(record);
            }

            var live = SplitEntries(expanded);
            live.UnionWith(SplitEntries(literal));

            if (live.Count == 0)
            {
                // The variable is absent or unreadable, so the ownership test has nothing
                // to run against. Fail closed rather than let the answer depend on the
                // ambient profile: deleting something that is not there is a no-op, so
                // refusing costs nothing, and it means the verdict for a system-critical
                // variable is the same on a fresh profile as on a configured one.
                return Refuse(
                    record,
                    "restore_env",
                    target,
                    ReplayRefusalCode.EnvironmentUnverifiable,
                    $"restore_env refused: deleting {r.Scope}-scope '{r.Name}' — its current " +
                    "value could not be read, and a variable the system depends on is never " +
                    "removed on an unverified claim");
            }

            foreach (var entry in live)
            {
                if (OwnedByThisInstall(entry, isMachine))
                {
                    continue;
                }
                return Refuse(
                    record,
                    "restore_env",
                    target,
                    ReplayRefusalCode.EnvironmentDeleteNotOwned,
                    $"restore_env refused: deleting {r.Scope}-scope '{r.Name}' would discard " +
                    $"'{entry}', which this install does not own");
            }
            return Allow(record);
        }

        if (r.PriorValue is null)
        {
            // Nothing is written and nothing is deleted.
            return Allow(record);
        }

        var currentEntries = SplitEntries(expanded);
        currentEntries.UnionWith(SplitEntries(literal));

        // Model (1): append / prepend. The install added entries to a value that already
        // existed, so restoring it puts back a SUBSET of what is there now — every entry
        // written is already present, or is a directory this install owns.
        var isSubsetRestore = true;
        string? foreignEntry = null;
        foreach (var entry in SplitEntries(r.PriorValue))
        {
            var alternate = ExpandSafely(entry);
            if (currentEntries.Contains(entry) ||
                currentEntries.Contains(alternate) ||
                OwnedByThisInstall(entry, isMachine) ||
                OwnedByThisInstall(alternate, isMachine))
            {
                continue;
            }
            isSubsetRestore = false;
            foreignEntry = entry;
            break;
        }

        if (isSubsetRestore)
        {
            return Allow(record);
        }

        // Model (2): action: set — the DOCUMENTED DEFAULT for env_set, and the case model
        // (1) alone can never accept. A `set` REPLACES the value, so the prior value is by
        // construction absent from the current one: a manifest that repoints machine
        // JAVA_HOME at its own JRE has a prior value of C:\Program Files\Java\jdk-21,
        // which is neither present now nor inside install_dir. Refusing it leaves
        // JAVA_HOME dangling at the directory the uninstall just deleted, and reports a
        // REFUSED record on a completely legitimate uninstall.
        //
        // The genuineness test that fits a whole-value replacement is the CURRENT value,
        // not the prior one: if everything the variable holds right now is a directory
        // this install owns, then this install had taken the variable over, and putting
        // back whatever preceded it is exactly the undo. A planted record cannot exploit
        // that — it would have to find a variable already pointing wholly inside a
        // directory this install owns, and for machine scope that directory must also be
        // admin-only writable.
        //
        // System-critical variables are excluded: no installer legitimately `set`s PATH,
        // and the subset rule must keep governing them.
        if (!IsSystemCriticalVariable(r.Name) &&
            currentEntries.Count > 0 &&
            AllOwnedByThisInstall(currentEntries, isMachine))
        {
            return Allow(record);
        }

        return Refuse(
            record,
            "restore_env",
            target,
            ReplayRefusalCode.EnvironmentIntroducesForeignEntry,
            $"restore_env refused: {r.Scope}-scope '{r.Name}' would introduce '{foreignEntry}', " +
            "which is neither already present in the variable nor a directory this install " +
            "owns, and the variable's current value is not one this install wholly owns " +
            "either — a restore may only remove what the install added, or put back what a " +
            "value this install had taken over used to hold");
    }

    private bool AllOwnedByThisInstall(HashSet<string> entries, bool isMachine)
    {
        foreach (var entry in entries)
        {
            if (!OwnedByThisInstall(entry, isMachine) &&
                !OwnedByThisInstall(ExpandSafely(entry), isMachine))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// A path this install may legitimately hand to the system as a place to load or run
    /// from: inside the install directory, and — for machine scope — only when no
    /// non-administrator can write it. The filesystem scope roots are excluded on
    /// purpose; they are user-writable, and a user-writable entry on the machine
    /// <c>PATH</c> — or in a machine-wide execution mapping — is the hijack.
    /// </summary>
    private bool OwnedByThisInstall(string? path, bool isMachine)
    {
        var full = Normalize(path);
        if (full is null || !IsUnder(full, _installDir))
        {
            return false;
        }

        if (!isMachine)
        {
            return true;
        }

        return OperatingSystem.IsWindows() && StateDirectorySecurity.IsAdminOnlyWritable(full);
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadEnv(bool isMachine, string name, bool expand)
    {
#pragma warning disable CA1031 // Fail closed: an unreadable value must not be treated as "matches".
        try
        {
            using var key = isMachine
                ? Registry.LocalMachine.OpenSubKey(MachineEnvKey, writable: false)
                : Registry.CurrentUser.OpenSubKey(UserEnvKey, writable: false);
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

    /// <summary>
    /// Environment variables whose removal breaks the machine or the session rather
    /// than merely un-configuring an application. Deleting one of these is treated as
    /// a destructive primitive and requires that this install own everything in it.
    /// </summary>
    private static bool IsSystemCriticalVariable(string name) =>
        Array.Exists(
            SystemCriticalVariables,
            v => v.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static readonly string[] SystemCriticalVariables =
    {
        "Path", "PATHEXT", "ComSpec", "windir", "SystemRoot", "SystemDrive",
        "TEMP", "TMP", "OS", "PSModulePath", "DriverData",
        "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE", "PROCESSOR_IDENTIFIER",
        "PROCESSOR_LEVEL", "PROCESSOR_REVISION",
        "USERNAME", "USERPROFILE", "APPDATA", "LOCALAPPDATA",
        "HOMEDRIVE", "HOMEPATH", "ProgramData", "ALLUSERSPROFILE",
        "ProgramFiles", "CommonProgramFiles",
    };

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
            return Refuse(
                record,
                "remove_service",
                r.ServiceName,
                ReplayRefusalCode.ServiceUnverifiable,
                $"remove_service refused: '{r.ServiceName}' cannot be verified off Windows");
        }

        var imagePath = ReadServiceImagePath(r.ServiceName);
        if (imagePath is null)
        {
            // No such service (or its key is unreadable and therefore not ours to
            // delete): sc delete would be a no-op.
            return Allow(record);
        }

        if (ImagePathIsInside(imagePath, _installDir))
        {
            return Allow(record);
        }

        return Refuse(
            record,
            "remove_service",
            r.ServiceName,
            ReplayRefusalCode.ServiceNotOwned,
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
    /// True when a service <c>ImagePath</c> runs a binary inside
    /// <paramref name="installDir"/>.
    /// </summary>
    /// <remarks>
    /// An <c>ImagePath</c> is a command line, not a path: it may be quoted, carry
    /// arguments, and use the <c>\??\</c> NT prefix. Both the parsed executable and the
    /// raw remainder are tested so an unquoted path containing spaces (the shape
    /// <c>C:\Program Files\App\svc.exe -k net</c>) is not misread as <c>C:\Program</c>
    /// and a legitimate service teardown refused. <c>internal</c> so the parsing — the
    /// part that decides whether a real uninstall survives — can be tested directly.
    /// </remarks>
    internal static bool ImagePathIsInside(string? imagePath, string installDir)
    {
        var root = Normalize(installDir);
        if (root is null)
        {
            return false;
        }

        foreach (var candidate in ProgramPathCandidates(imagePath))
        {
            var full = Normalize(candidate);
            if (full is not null && IsUnder(full, root))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The path(s) a command line could be naming as its program, most specific first.
    /// </summary>
    /// <remarks>
    /// A service <c>ImagePath</c> and a shell-verb <c>command</c> are both command lines,
    /// not paths: quoted or not, with or without arguments, occasionally carrying the
    /// <c>\??\</c> NT prefix. Both the parsed executable and the raw remainder are
    /// returned, so an unquoted path containing spaces — <c>C:\Program Files\App\svc.exe
    /// -k net</c> — is not misread as <c>C:\Program</c> and a legitimate teardown refused.
    /// Shared by the service rule and the execution-mapping rule so the two cannot drift.
    /// </remarks>
    private static string[] ProgramPathCandidates(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return Array.Empty<string>();
        }

        var value = commandLine.Trim();
        if (value.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            value = value.Substring(4);
        }

        if (value.StartsWith('"'))
        {
            var close = value.IndexOf('"', 1);
            var quoted = close > 1 ? value.Substring(1, close - 1) : value.Trim('"');
            return new[] { quoted };
        }

        var space = value.IndexOf(' ', StringComparison.Ordinal);
        return space > 0
            ? new[] { value.Substring(0, space), value }
            : new[] { value };
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
            return Refuse(
                record,
                "unregister_com",
                r.DllPath,
                ReplayRefusalCode.ComDllOutsideInstallDir,
                $"unregister_com refused: '{r.DllPath}' {OutsideText()} — a DLL outside " +
                "the install directory must never be loaded by the elevated process");
        }

        return Allow(r with { DllPath = rederived });
    }

    /// <summary>
    /// Re-derive <paramref name="recordedPath"/> as <c>install_dir</c> + the path
    /// relative to it, returning <c>null</c> when it does not resolve inside the
    /// install directory. Normalization happens before the containment test, so
    /// <c>&lt;install_dir&gt;\..\..\Users\Public\evil.dll</c> is refused.
    /// </summary>
    private string? ReDeriveInsideInstallDir(string? recordedPath)
    {
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

    private string OutsideText() =>
        $"is outside the install directory '{_installDir}' and the scope roots the " +
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
