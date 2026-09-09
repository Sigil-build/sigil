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
    /// Sub-paths that no record may WRITE into, even though their containing root is
    /// allowed. The Start Menu is allowed because <c>shortcut_create</c> writes there and
    /// its reversal must replay — but the Start Menu's own subtree contains
    /// <c>Programs\Startup</c>, a per-logon execution surface that a planted
    /// <c>restore_deleted_file</c> would otherwise be able to populate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Write-only: see <see cref="IsAllowedPath"/>. Deleting from these paths stays
    /// allowed, because <c>shortcut_create</c> accepts an explicit location and a
    /// publisher may legitimately place a startup shortcut there — whose removal at
    /// uninstall must replay, or the app keeps auto-starting after it has been removed.
    /// </para>
    /// <para>
    /// <strong>The permit side cannot tell whose entry it is deleting.</strong> Nothing in
    /// a <c>delete_shortcut</c> record establishes that the install created the file it
    /// names, so a planted journal can delete ANY all-users Startup entry, and
    /// <c>remove_directory</c> can remove the folder itself once empty. That is
    /// destructive rather than elevating — it can disable another program's auto-start, not
    /// run anything — and it is the trade the write/delete asymmetry deliberately makes:
    /// the alternative leaves the installer's own startup shortcut running forever after
    /// uninstall. Disclosed rather than discovered.
    /// </para>
    /// </remarks>
    private static readonly string[] ExcludedRootSubPaths =
    {
        @"Programs\Startup",
        "Startup",
    };

    private readonly string _installDir;
    private readonly string[] _fileRoots;
    private readonly string[] _excludedRoots;

    /// <summary>
    /// The out-of-tree destinations the SIGNED BLOB declares with
    /// <c>allow_outside_install_dir</c>, canonicalized and vetted by
    /// <see cref="DeclaredRootFloor"/> (R44).
    /// </summary>
    /// <remarks>
    /// Held separately from <see cref="_fileRoots"/> rather than merged into it, for two
    /// reasons that are both load-bearing. These roots are matched with
    /// <see cref="PathContainment.IsUnderWithoutTraversal"/> — a junction planted under a
    /// declared root must not redirect a replayed write out of it — and they must never
    /// leak into <see cref="OwnedByThisInstall"/>, which is what keeps a machine
    /// <c>PATH</c> entry and a machine-wide execution mapping pinned to the install
    /// directory no matter what a manifest declares.
    /// </remarks>
    private readonly string[] _declaredRoots;

    /// <summary>
    /// The registry keys the SIGNED BLOB's registry steps name, folded to the canonical
    /// form <see cref="EffectiveRegistryPath"/> produces (R51). A registry record may
    /// replay only at or under one of these.
    /// </summary>
    private readonly string[] _declaredRegistryKeys;

    /// <summary>
    /// Environment values as they were BEFORE this replay began, keyed by
    /// <c>machine|user</c> + <c>expanded|literal</c> + name, filled on first read and
    /// never refreshed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This cache is a security mechanism, not a performance optimisation. Do not
    /// replace it with a live read.</strong>
    /// </para>
    /// <para>
    /// <see cref="EnvVerdict"/>'s whole-value ("<c>action: set</c>") model asks whether
    /// this install had taken a variable over, and answers it from the variable's current
    /// content. Read live, that question is answerable by the replay itself: the loop in
    /// <see cref="RollbackJournal.UndoAsync"/> applies records one at a time, so a first
    /// record may legitimately write an install-owned value into a variable and a second
    /// record then finds exactly the precondition the set model looks for. The pair
    /// composes into "set any non-critical machine variable to anything" — a code
    /// injection vector for variables such as <c>COR_PROFILER_PATH</c> — while each record
    /// is individually plausible and neither is sufficient alone.
    /// </para>
    /// <para>
    /// Anchoring the premise to the pre-replay snapshot removes the composition: no record
    /// can manufacture the precondition for a later one, because the answer is fixed
    /// before the first record runs. Legitimate append/prepend sequences are unaffected —
    /// their prior values are subsets of the pre-replay value by construction.
    /// </para>
    /// </remarks>
    private readonly Dictionary<string, string?> _envSnapshot =
        new(StringComparer.OrdinalIgnoreCase);

    private ReplayAnchor(
        string installDir,
        string[] fileRoots,
        string[] excludedRoots,
        string[] declaredRoots,
        string[] declaredRegistryKeys,
        IReadOnlyList<string> notices)
    {
        _installDir = installDir;
        _fileRoots = fileRoots;
        _excludedRoots = excludedRoots;
        _declaredRoots = declaredRoots;
        _declaredRegistryKeys = declaredRegistryKeys;
        Notices = notices;
    }

    /// <summary>
    /// Operator-facing lines about the DECLARATIONS themselves — a declared destination
    /// that could not be resolved at uninstall time, or one the floor refused to anchor
    /// with. Reported once by <see cref="RollbackJournal.UndoAsync"/> before the replay
    /// starts.
    /// </summary>
    /// <remarks>
    /// These are not record refusals and they are not failures. They exist because the
    /// alternative — dropping a declaration silently — is how "the publisher declared it,
    /// the anchor never saw it, the uninstall left files behind and said nothing" happens.
    /// </remarks>
    public IReadOnlyList<string> Notices { get; }

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

        // R44 / R51: the only widening input, and it comes from the SIGNED BLOB.
        //
        // Resolved HERE rather than by the caller, and with `installDir` — the directory
        // UninstallEngine chose, which is the one RECORDED at install time. A declared
        // `{install_dir}\…` destination expanded against a recomputed default would name
        // a directory the install never wrote to. Nothing in this block reads the
        // journal: the anchorage carries the declarations, the journal carries records,
        // and the two never meet except when a record is CHECKED against them.
        var declared = anchorage.Declarations.Resolve(installDir);
        var notices = new List<string>(declared.Notices);

        var declaredRoots = new List<string>();
        foreach (var destination in declared.Destinations)
        {
            var vetted = DeclaredRootFloor.Vet(destination, out var rejection);
            if (vetted is null)
            {
                notices.Add(
                    "the declared out-of-tree destination is not anchored: " + rejection +
                    " — records naming it are refused, and anything the install wrote there " +
                    "must be removed from an 'uninstall:' step instead");
                continue;
            }

            if (!declaredRoots.Contains(vetted, StringComparer.OrdinalIgnoreCase))
            {
                declaredRoots.Add(vetted);
            }
        }

        var declaredKeys = new List<string>();
        foreach (var key in declared.RegistryKeys)
        {
            // Folded through the SAME function record keys go through, so a declaration
            // and a record that name the same coordinate in different spellings
            // (HKCR\Foo vs HKLM\Software\Classes\Foo, a WOW6432Node view) still match.
            var effective = EffectiveRegistryPath(key.Hive, key.Key);
            if (string.IsNullOrEmpty(effective))
            {
                notices.Add(
                    $"the declared registry key '{key.Hive}\\{key.Key}' is not in a hive an " +
                    "installer's rollback may reverse, so records naming it are refused");
                continue;
            }

            if (!declaredKeys.Contains(effective, StringComparer.OrdinalIgnoreCase))
            {
                declaredKeys.Add(effective);
            }
        }

        return new ReplayAnchor(
            installDir,
            roots.ToArray(),
            excluded.ToArray(),
            declaredRoots.ToArray(),
            declaredKeys.ToArray(),
            notices);
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
            //
            // `writesContent` also decides whether the excised sub-roots apply: a record
            // that only DELETES may reach them (removing a Startup shortcut this install
            // created is the legitimate uninstall), a record that WRITES may not.
            case RollbackRecord.RestoreFile r:
                return PathVerdict(
                    record,
                    "restore_file",
                    r.Path,
                    // With no usable backup this record deletes Path rather than writing it.
                    writesContent: r is { ExistedBefore: true, BackupPath: not null },
                    contentSource: r.BackupPath,
                    withSource: static (rec, s) => ((RollbackRecord.RestoreFile)rec) with { BackupPath = s });

            case RollbackRecord.RemoveDirectory r:
                return PathVerdict(record, "remove_directory", r.Path, writesContent: false);

            case RollbackRecord.DeleteShortcut r:
                return PathVerdict(record, "delete_shortcut", r.Path, writesContent: false);

            case RollbackRecord.RemoveUninstaller r:
                return PathVerdict(record, "remove_uninstaller", r.Path, writesContent: false);

            case RollbackRecord.RestoreDeletedFile r:
                return PathVerdict(
                    record,
                    "restore_deleted_file",
                    r.OriginalPath,
                    writesContent: true,
                    contentSource: r.StashPath,
                    withSource: static (rec, s) =>
                        ((RollbackRecord.RestoreDeletedFile)rec) with { StashPath = s! });

            case RollbackRecord.RestoreDeletedDirectory r:
                return PathVerdict(
                    record,
                    "restore_deleted_directory",
                    r.OriginalPath,
                    writesContent: true,
                    contentSource: r.StashPath,
                    withSource: static (rec, s) =>
                        ((RollbackRecord.RestoreDeletedDirectory)rec) with { StashPath = s! });

            case RollbackRecord.RestoreConfigFile r:
                return PathVerdict(
                    record,
                    "restore_config_file",
                    r.OriginalPath,
                    // A null stash means "the edit created this file"; the undo deletes it.
                    writesContent: r.StashPath is not null,
                    contentSource: r.StashPath,
                    withSource: static (rec, s) =>
                        ((RollbackRecord.RestoreConfigFile)rec) with { StashPath = s });

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
    /// A source outside the anchored roots is only REFUSED when it currently EXISTS.
    /// <c>file_delete</c>, <c>directory_delete</c> and the config editors stash the prior
    /// content under <c>%TEMP%\sigil-fd-*</c> / <c>-dd-*</c> / <c>-cfg-*</c>, and
    /// <c>RollbackJournal.DiscardTransientStashes</c> reclaims those the moment the
    /// install commits — but the RECORD, stash path and all, is persisted. Refusing on the
    /// path alone therefore refused every persisted <c>file_delete</c>,
    /// <c>directory_delete</c>, <c>ini_write</c>, <c>json_edit</c> and <c>xml_edit</c>
    /// record on a perfectly healthy uninstall, and emitted the very log line the
    /// documentation tells publishers to investigate.
    /// </para>
    /// <para>
    /// When the source is absent the record is ALLOWED with its source REWRITTEN to a path
    /// that cannot exist. That keeps the log quiet for the ordinary case and closes the
    /// check-then-copy race in the same move: a source that appears between this check and
    /// the copy is no longer the one the record will read. A source that exists AND is out
    /// of range is still refused and logged — that is the planted case.
    /// </para>
    /// <para>
    /// <strong>Rewriting is not always a no-op, and the difference is worth stating.</strong>
    /// For <c>restore_deleted_file</c>, <c>restore_deleted_directory</c> and
    /// <c>restore_config_file</c> the undo tests its stash for existence and returns, so a
    /// rewritten source really does make the record do nothing. <c>restore_file</c> does
    /// not: with no usable backup it falls through to <c>File.Delete(Path)</c>
    /// (<c>RollbackRecord.RestoreFile.UndoAsync</c>), so a planted record that would
    /// previously have been refused now DELETES its destination instead. That is accepted
    /// rather than overlooked — the destination has already passed the containment check
    /// above, and the identical deletion is reachable anyway through
    /// <c>RestoreFile(ExistedBefore: false)</c>, which the anchor allows by design. So no
    /// new primitive; a deletion inside the anchor, which was always permitted.
    /// </para>
    /// <para>
    /// A <c>restore_file</c> backup is written as <c>&lt;destination&gt;.sigil-bak</c>
    /// (<c>FileCopyStep</c>), so it is anchored exactly when its destination is and never
    /// reaches either branch. Mid-install rollback runs
    /// <see cref="ReplayAnchorage.InProcess"/> and is unaffected throughout.
    /// </para>
    /// </remarks>
    private Verdict PathVerdict(
        RollbackRecord record,
        string type,
        string path,
        bool writesContent = false,
        string? contentSource = null,
        Func<RollbackRecord, string?, RollbackRecord>? withSource = null)
    {
        if (!IsAllowedPath(path, writesContent))
        {
            return Refuse(
                record,
                type,
                path,
                ReplayRefusalCode.PathOutsideInstallRoots,
                $"{type} refused: '{path}' {OutsideText()}" +
                (writesContent && IsExcised(path)
                    ? " — a record may remove what this install placed there, but never write to it"
                    : string.Empty));
        }

        if (contentSource is null || IsAllowedPath(contentSource, forWrite: false))
        {
            return Allow(record);
        }

        if (SourceExists(contentSource))
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

        // Absent: allow, with the source rewritten so an ordinary uninstall logs nothing
        // and nothing that appears later can be read. See the remarks for what "rewritten"
        // means per record type — for restore_file it is a deletion, not a no-op.
        //
        // NOTE for anyone adding a content-bearing record: passing no `withSource` allows
        // the record UNCHANGED, which leaves the check-then-copy race open for it. Every
        // caller today supplies one; that is a constraint on new call sites, not an
        // invariant this method enforces.
        return withSource is null
            ? Allow(record)
            : Allow(withSource(record, NeuteredSourcePath()));
    }

    /// <summary>A path inside the anchor that is guaranteed not to exist.</summary>
    private string NeuteredSourcePath() =>
        Path.Combine(_installDir, "sigil-neutered-source-" + Guid.NewGuid().ToString("N"));

    private static bool SourceExists(string source)
    {
#pragma warning disable CA1031 // Fail closed: if existence cannot be determined, treat it as present.
        try
        {
            return File.Exists(source) || Directory.Exists(source);
        }
        catch
        {
            return true;
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// True when <paramref name="path"/> resolves inside one of the anchored
    /// filesystem roots. Normalizes first, so <c>&lt;install_dir&gt;\..\..\Windows</c>
    /// cannot pass, and requires a directory separator after the root, so
    /// <c>C:\rootevil</c> cannot pass as <c>C:\root</c>.
    /// </summary>
    /// <remarks>
    /// The excised sub-roots apply only when <paramref name="forWrite"/> is true, and the
    /// asymmetry is the point. Excising <c>Programs\Startup</c> exists to stop a planted
    /// record WRITING an executable into a per-logon execution surface. Applying it to
    /// deletes as well inverted the intent: a <c>shortcut_create</c> with an explicit path
    /// into the all-users Startup folder is documented and supported, and refusing its
    /// <c>delete_shortcut</c> record left the shortcut auto-starting after uninstall — the
    /// anchor creating the persistence it was added to prevent.
    /// </remarks>
    private bool IsAllowedPath(string? path, bool forWrite)
    {
        var full = Normalize(path);
        if (full is null)
        {
            return false;
        }

        if (forWrite && IsExcised(full))
        {
            return false;
        }

        foreach (var root in _fileRoots)
        {
            if (IsUnder(full, root))
            {
                return true;
            }
        }

        // R44: the destinations the signed manifest declared with
        // `allow_outside_install_dir`. Checked LAST, so a path already inside the install
        // directory or a scope root reaches the same verdict it did before this lane —
        // widening the anchor cannot change an existing answer, only add new ones.
        //
        // The stronger predicate is deliberate. A declared root such as
        // C:\ProgramData\MyApp typically inherits BUILTIN\Users write access, so a
        // junction planted INSIDE it is a realistic way to redirect a replayed write out
        // of it; IsUnderWithoutTraversal walks the chain and refuses that.
        foreach (var declaredRoot in _declaredRoots)
        {
            if (PathContainment.IsUnderWithoutTraversal(declaredRoot, full))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when the path is in a sub-root nothing may WRITE to.</summary>
    private bool IsExcised(string? path)
    {
        var full = Normalize(path);
        if (full is null)
        {
            return false;
        }
        foreach (var excludedRoot in _excludedRoots)
        {
            if (IsUnder(full, excludedRoot))
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

        // R51: the key must be one the SIGNED MANIFEST names. This is the check that
        // closes the class the denylist above could not: `txtfile`, `lnkfile`, `mscfile`,
        // `Drivers32`, `App Paths`, and every key shape nobody has thought of yet are all
        // refused by the same rule, because none of them was declared — no name of theirs
        // appears anywhere in this file, and none has to.
        if (!IsDeclaredRegistryKey(effective))
        {
            return Refuse(
                record,
                type,
                target,
                ReplayRefusalCode.RegistryKeyNotDeclared,
                $"{type} refused: '{target}' is not a key this application's signed manifest " +
                "declares a registry step for, so nothing in this installation can have " +
                "created it — only the manifest's own keys may be reversed from a journal " +
                "read off disk");
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
    /// <para>
    /// This predicate is now the FLOOR, not the whole rule. It says where an installer's
    /// rollback may operate at all; <see cref="IsDeclaredRegistryKey"/> then says which
    /// keys inside that space this particular application declared (R51). Both must pass.
    /// </para>
    /// <para>
    /// It is kept rather than deleted, and the deny list with it, as defence in depth. It
    /// is independent of the manifest: a manifest that declares
    /// <c>Software\Microsoft\Windows\CurrentVersion\Run</c> — by mistake, or because the
    /// publisher's signing key was misused — still cannot get an auto-run key replayed
    /// from a journal. One layer answers "did this application declare this?", the other
    /// answers "may any installer's rollback touch this at all?", and the second question
    /// does not stop being worth asking just because the first one now has an answer.
    /// </para>
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
    /// True when <paramref name="normalizedKey"/> is at or under a key the signed
    /// manifest's own registry steps name (R51).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>An allowlist, and the allowance comes from the blob — never the record.</strong>
    /// The three registry steps (<c>registry_write</c>, <c>registry_delete_value</c>,
    /// <c>registry_delete_key</c>) are the only producers of the two registry record
    /// types, so a key no step declares is a key no honest journal can hold a record for.
    /// A record naming one is either a planted entry or a step type that started
    /// journaling registry records without being added to
    /// <c>SignedDeclarations.CollectFrom</c> — and
    /// <c>RegistryRecordProducerTests</c> fails the build for the second case.
    /// </para>
    /// <para>
    /// The match is by SUBTREE, not by exact key. <c>registry_delete_key</c> with
    /// <c>recursive: true</c> reverses into records for keys below the declared one, and
    /// a <c>registry_write</c> creating <c>…\App\Settings</c> under a declared
    /// <c>…\App</c> is the ordinary shape of a manifest. Exact matching would refuse both
    /// on a perfectly healthy uninstall.
    /// </para>
    /// <para>
    /// With no declarations at all, nothing matches and every registry record is refused.
    /// That is the intended fail-closed direction: an anchored replay with no signed
    /// artefact behind it has no way to tell an application's own key from anyone else's.
    /// </para>
    /// </remarks>
    private bool IsDeclaredRegistryKey(string normalizedKey)
    {
        foreach (var declared in _declaredRegistryKeys)
        {
            if (IsUnderRegistryPrefix(normalizedKey, declared))
            {
                return true;
            }
        }
        return false;
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
        //
        // Read from the PRE-REPLAY snapshot, never live — see _envSnapshot for why a live
        // read lets two records compose into a primitive neither of them is.
        var expanded = SnapshotEnv(isMachine, r.Name, expand: true);
        var literal = SnapshotEnv(isMachine, r.Name, expand: false);

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

        // A variable the system depends on is never eligible for the whole-value model
        // below: no installer legitimately `set`s PATH or ComSpec, and the subset rule must
        // keep governing them. Checked BEFORE the model rather than as one conjunct inside
        // it, and refused with its OWN code, so that deleting this guard changes the
        // observable outcome — as one conjunct it was unfalsifiable, because a
        // system-critical variable's value is never wholly install-owned anyway and the
        // model would have refused on that instead.
        if (IsSystemCriticalVariable(r.Name))
        {
            return Refuse(
                record,
                "restore_env",
                target,
                ReplayRefusalCode.EnvironmentSystemVariableNotReplaceable,
                $"restore_env refused: {r.Scope}-scope '{r.Name}' is a variable the system " +
                $"depends on, so it may only have entries removed; restoring '{foreignEntry}' " +
                "would replace its contents wholesale");
        }

        // Model (2): action: set — the DOCUMENTED DEFAULT for env_set, and the case model
        // (1) alone can never accept. A `set` REPLACES the value, so the prior value is by
        // construction absent from the current one: a manifest that repoints machine
        // JAVA_HOME at its own JRE has a prior value of C:\Program Files\Java\jdk-21,
        // which is neither present now nor inside install_dir. Refusing it leaves
        // JAVA_HOME dangling at the directory the uninstall just deleted, and reports a
        // REFUSED record on a completely legitimate uninstall.
        //
        // The genuineness test that fits a whole-value replacement is the value the
        // variable held BEFORE THIS REPLAY BEGAN (see _envSnapshot — a live read lets an
        // earlier record manufacture this precondition for a later one): if everything it
        // held then is a directory this install owns, this install had taken the variable
        // over, and putting back whatever preceded it is exactly the undo.
        if (currentEntries.Count > 0 &&
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
    /// <remarks>
    /// <para>
    /// <strong>This predicate reads live system state and can be perturbed by an earlier
    /// record in the same replay — in the PERMISSIVE direction.</strong> Stated precisely
    /// because an earlier version of the lane's audit had the direction backwards, and a
    /// future reader adding a predicate here will reason from it.
    /// </para>
    /// <para>
    /// <see cref="StateDirectorySecurity.IsAdminOnlyWritable"/> evaluates the containing
    /// directory when its target does not exist. So a <c>remove_directory</c> record
    /// replayed earlier can delete a user-writable subdirectory of the install directory,
    /// after which this predicate judges that path by the install directory's own ACL —
    /// answering <c>true</c> where the still-present subdirectory would have answered
    /// <c>false</c>. It gets looser, not stricter.
    /// </para>
    /// <para>
    /// It is nonetheless not exploitable, for a reason independent of the drift: the
    /// containment test above runs first and is unaffected by deletion, so the drift can
    /// only ever apply to a path already inside the install directory. Turning such a path
    /// into a usable machine <c>PATH</c> entry or execution-mapping target requires
    /// re-creating it, and for a machine install the install directory is admin-only
    /// writable — precisely the privilege an unprivileged attacker does not have. For user
    /// scope the ACL half is not consulted at all. Pre-existing and left alone; recorded so
    /// the next predicate that reads live state is judged on its own merits rather than by
    /// analogy to this one.
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// The value <paramref name="name"/> held before this replay started. Reads through
    /// to the registry once and memoizes; see <see cref="_envSnapshot"/> for why the
    /// memoization is load-bearing.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private string? SnapshotEnv(bool isMachine, string name, bool expand)
    {
        var key = $"{(isMachine ? "machine" : "user")}|{(expand ? "expanded" : "literal")}|{name}";
        if (!_envSnapshot.TryGetValue(key, out var value))
        {
            value = ReadEnv(isMachine, name, expand);
            _envSnapshot[key] = value;
        }
        return value;
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
        $"is outside the install directory '{_installDir}', the scope roots the " +
        "installer legitimately writes, and the out-of-tree destinations this " +
        "application's signed manifest declares";

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
