using System.Linq;
using System.Runtime.Versioning;
using Microsoft.Win32;
using SigilBuild.Wrapper.Steps;

namespace SigilBuild.Wrapper.Engine;

/// <summary>
/// Append-only log of rollback actions recorded by individual steps as they
/// mutate the system. <see cref="UndoAsync"/> walks the records in reverse
/// (LIFO) on a failed install. Future tasks (19) serialize the journal as
/// <c>uninstall.json</c> for post-install removal.
/// </summary>
public sealed class RollbackJournal
{
    private readonly System.Collections.Generic.List<RollbackRecord> _records = new();

    public System.Collections.Generic.IReadOnlyList<RollbackRecord> Records => _records;

    public void Append(RollbackRecord record)
    {
        System.ArgumentNullException.ThrowIfNull(record);
        _records.Add(record);
    }

    /// <summary>
    /// Delete the transient install-time <em>stash</em> artefacts once the install
    /// has COMMITTED successfully. A <c>file_delete</c> / <c>directory_delete</c>
    /// step copies its target to a <c>%TEMP%</c> stash (<c>sigil-fd-*</c> /
    /// <c>sigil-dd-*</c>) so a mid-install rollback can restore the original; the
    /// stash is normally reclaimed by <see cref="UndoAsync"/> on rollback. On the
    /// SUCCESS path no rollback runs, so without this call the stash leaks into
    /// <c>%TEMP%</c> forever (an empty <c>sigil-dd-*</c> directory for an
    /// empty-directory delete, a stray <c>sigil-fd-*</c> file otherwise).
    /// <para>
    /// Discarding is safe: the two stash-bearing records
    /// (<see cref="RollbackRecord.RestoreDeletedFile"/> /
    /// <see cref="RollbackRecord.RestoreDeletedDirectory"/>) are NOT part of the
    /// persisted <c>uninstall.json</c> schema (they have no
    /// <c>SerializableRollbackRecord</c> mapping), so their <c>%TEMP%</c> stash was
    /// never meant to outlive the install run. Best-effort and idempotent.
    /// </para>
    /// <para>
    /// <strong>R28 — the omission of <see cref="RollbackRecord.RestoreFile"/> here is
    /// deliberate and must stay.</strong> Its <c>.sigil-bak</c> is the pre-existing
    /// content of a file the install OVERWROTE, so it has to outlive the run: it is the
    /// only thing that lets uninstall put the user's original file back. Discarding it
    /// would silently drop that capability. What it was missing was a lifecycle, and that
    /// is <see cref="RelocateCommittedStashes"/>: at commit the stash moves out of the
    /// install directory into the per-app state directory, and uninstall consumes it
    /// there.
    /// </para>
    /// </summary>
    public void DiscardTransientStashes()
    {
        foreach (var record in _records)
        {
            switch (record)
            {
                case RollbackRecord.RestoreDeletedFile f:
                    TryDeleteFile(f.StashPath);
                    break;
                case RollbackRecord.RestoreDeletedDirectory d:
                    TryDeleteDirectory(d.StashPath);
                    break;
                case RollbackRecord.RestoreConfigFile { StashPath: { } cfgStash }:
                    // P8: the prior-content stash of an ini/json/xml edit — reclaim
                    // it on the success path (a config edit isn't reversed on uninstall).
                    TryDeleteFile(cfgStash);
                    break;
                default:
                    break;
            }
        }
    }

    /// <summary>
    /// R28 — the <c>.sigil-bak</c> contract. Move every surviving <c>restore_file</c>
    /// backup out of the install directory into <paramref name="stashRoot"/> and rewrite
    /// the records to point at their new home, so a COMMITTED install leaves no
    /// <c>.sigil-bak</c> beside the files it replaced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The decision, written down.</strong> These stashes are not litter to be
    /// deleted: each one is the pre-existing content of a file the install overwrote, and
    /// it is what lets uninstall put that file back.
    /// <see cref="DiscardTransientStashes"/> skipping <c>RestoreFile</c> is therefore
    /// CORRECT, and the register's accurate framing — "retained by design, with no
    /// lifecycle story" — is the thing to fix. Discarding them on the success path would
    /// trade a real capability (restoring a file the publisher never shipped) for
    /// tidiness. So they are kept, and given the lifecycle they were missing:
    /// </para>
    /// <list type="bullet">
    ///   <item>during the install they stay beside their destination, where a mid-install
    ///   rollback — which runs unanchored and in-process — restores from them;</item>
    ///   <item>at commit they move into the per-app state directory,
    ///   <c>&lt;StateRoot&gt;\Sigil\&lt;AppId&gt;\backups</c>: out of Program Files, and
    ///   hardened to administrators-only in machine scope by the same S1 code that
    ///   protects <c>uninstall.json</c>;</item>
    ///   <item>they then live exactly as long as the install does — uninstall's
    ///   <c>RestoreFile</c> undo copies each one back and deletes it, and whatever it
    ///   does not consume goes with the state directory.</item>
    /// </list>
    /// <para>
    /// The per-app state directory is an anchored replay root under
    /// <see cref="ReplayAnchorage.ForInstall"/>, so a relocated stash is still a legal
    /// content source at uninstall time — and a NARROWER one than the install tree,
    /// because the allowance is this app's own directory alone.
    /// </para>
    /// <para>
    /// Best-effort per record: a stash that will not move keeps its old home AND its
    /// record, so the worst case is the previous behaviour for one file rather than a
    /// lost restore.
    /// </para>
    /// </remarks>
    public void RelocateCommittedStashes(string stashRoot)
    {
        System.ArgumentException.ThrowIfNullOrWhiteSpace(stashRoot);

        for (var i = 0; i < _records.Count; i++)
        {
            if (_records[i] is not RollbackRecord.RestoreFile
                {
                    ExistedBefore: true, BackupPath: { Length: > 0 } backup
                } record)
            {
                continue;
            }

#pragma warning disable CA1031 // Best-effort: a stash that will not move keeps its old home and its record.
            try
            {
                if (!System.IO.File.Exists(backup))
                {
                    continue;
                }

                System.IO.Directory.CreateDirectory(stashRoot);

                // Named from the destination so a repeated install of the same file
                // overwrites its own stash rather than accumulating one per run, and two
                // directories' config.ini never collide.
                var moved = System.IO.Path.Combine(stashRoot, StashNameFor(record.Path));
                System.IO.File.Copy(backup, moved, overwrite: true);
                System.IO.File.Delete(backup);
                _records[i] = record with { BackupPath = moved };
            }
            catch
            {
                // Leave the record pointing at the stash that is still there.
            }
#pragma warning restore CA1031
        }
    }

    /// <summary>
    /// A stable, collision-free stash name for <paramref name="destination"/>: the
    /// destination's own file name, so a human can tell what it is, plus a hash of its
    /// full path, so two directories' <c>config.ini</c> do not share one stash.
    /// </summary>
    private static string StashNameFor(string destination)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(destination.ToUpperInvariant()));
        var tag = System.Convert.ToHexString(hash, 0, 8);
        var leaf = System.IO.Path.GetFileName(destination);
        if (string.IsNullOrEmpty(leaf))
        {
            leaf = "file";
        }
        return $"{leaf}.{tag}.sigil-bak";
    }

    private static void TryDeleteFile(string path)
    {
#pragma warning disable CA1031 // Best-effort temp cleanup; a leftover stash is harmless.
        try
        {
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
        catch { /* best-effort */ }
#pragma warning restore CA1031
    }

    private static void TryDeleteDirectory(string path)
    {
#pragma warning disable CA1031 // Best-effort temp cleanup; a leftover stash is harmless.
        try
        {
            if (System.IO.Directory.Exists(path))
            {
                System.IO.Directory.Delete(path, recursive: true);
            }
        }
        catch { /* best-effort */ }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Replay every record in reverse (LIFO). An individual failure does not cascade —
    /// the remaining records are still replayed — but it is no longer <em>swallowed</em>:
    /// it is reported on <paramref name="progress"/> and returned in
    /// <see cref="UndoOutcome.FailedRecords"/> so the caller can decline to report a
    /// success it did not achieve (register row R15).
    /// </summary>
    /// <param name="anchorage">
    /// Whether the records must be anchored before replay, and to what (R1).
    /// <strong>No default, on purpose</strong>: a caller replaying persisted state
    /// cannot lose anchoring by omitting an argument — only by writing
    /// <see cref="ReplayAnchorage.InProcess"/> and being wrong about it. A record whose
    /// target escapes the anchor is skipped, logged, and returned in
    /// <see cref="UndoOutcome.RefusedRecords"/>.
    /// </param>
    public async System.Threading.Tasks.Task<UndoOutcome> UndoAsync(
        ReplayAnchorage anchorage,
        System.IProgress<StepProgress>? progress = null,
        System.Threading.CancellationToken ct = default)
    {
        var anchor = ReplayAnchor.For(anchorage);
        var refused = new System.Collections.Generic.List<RefusedRecord>();
        var failed = new System.Collections.Generic.List<FailedRecord>();

        // Walk in reverse. Undo failures should not cascade — log and continue.
        var total = _records.Count;
        var completed = 0;
        for (var i = _records.Count - 1; i >= 0; i--)
        {
            ct.ThrowIfCancellationRequested();
            var record = _records[i];

            if (anchor is not null)
            {
                var verdict = anchor.Check(record);
                if (verdict.Refusal is not null)
                {
                    // Logged and skipped, never silent and never fatal: silence would
                    // mask an attack, and aborting would let one planted record block a
                    // legitimate uninstall.
                    refused.Add(verdict.Refusal);
                    completed++;
                    progress?.Report(new StepProgress(
                        completed, total, $"refused: {verdict.Refusal.Message}", IsError: true));
                    continue;
                }
                // The verdict may hand back a re-derived record (unregister_com's DLL
                // path is rebuilt from install_dir rather than trusted).
                record = verdict.Record;
            }

            try
            {
                await record.UndoAsync(ct).ConfigureAwait(false);
            }
            catch (System.OperationCanceledException)
            {
                // A genuine cancel is the caller's business, not a per-record failure.
                throw;
            }
            catch (UndoFailedException ex)
            {
                // The undo ran and did NOT achieve its goal: the service / task /
                // firewall rule / COM registration is still there. R15 — the one
                // outcome that must never be reported as a success.
                completed++;
                failed.Add(new FailedRecord(WireTypeOf(record), ex.Target, ex.Code, ex.Message));
                progress?.Report(new StepProgress(
                    completed, total, $"FAILED: {ex.Message}", IsError: true));
                continue;
            }
#pragma warning disable CA1031 // A failing record must not cascade — but it IS recorded (R15), not swallowed.
            catch (System.Exception ex)
            {
                completed++;
                var message =
                    $"{DescribeUndo(record)} could not be reversed — " +
                    $"{ex.GetType().Name}: {ex.Message}";
                failed.Add(new FailedRecord(
                    WireTypeOf(record), TargetOf(record), UndoFailureCode.RecordThrew, message));
                progress?.Report(new StepProgress(completed, total, $"FAILED: {message}", IsError: true));
                continue;
            }
#pragma warning restore CA1031
            completed++;
            progress?.Report(new StepProgress(completed, total, DescribeUndo(record), IsError: false));
        }

        return new UndoOutcome(refused, failed);
    }

    /// <summary>
    /// The record's wire discriminator — the same string
    /// <c>SerializableRollbackRecord.Type</c> uses — so a consumer can group failures by
    /// kind without parsing prose. Kept as a local switch rather than routed through
    /// <c>ToSerializable()</c>, which throws for the transient stash records that never
    /// reach <c>uninstall.json</c>.
    /// </summary>
    private static string WireTypeOf(RollbackRecord record) => record switch
    {
        RollbackRecord.RestoreFile => "restore_file",
        RollbackRecord.RemoveDirectory => "remove_directory",
        RollbackRecord.DeleteShortcut => "delete_shortcut",
        RollbackRecord.RestoreRegistryValue => "restore_registry_value",
        RollbackRecord.RestoreRegistryKey => "restore_registry_key",
        RollbackRecord.RestoreEnv => "restore_env",
        RollbackRecord.RestoreDeletedFile => "restore_deleted_file",
        RollbackRecord.RestoreDeletedDirectory => "restore_deleted_directory",
        RollbackRecord.RestoreConfigFile => "restore_config_file",
        RollbackRecord.RemoveUninstaller => "remove_uninstaller",
        RollbackRecord.RemoveService => "remove_service",
        RollbackRecord.DeleteScheduledTask => "delete_scheduled_task",
        RollbackRecord.UnregisterCom => "unregister_com",
        RollbackRecord.DeleteFirewallRule => "delete_firewall_rule",
        _ => "unknown",
    };

    /// <summary>
    /// The coordinate the record acts on, in the same natural form
    /// <see cref="RefusedRecord.Target"/> uses. Declared fields only — never a resolved
    /// parameter value — so a secret can never leak into a failure line.
    /// </summary>
    private static string TargetOf(RollbackRecord record) => record switch
    {
        RollbackRecord.RestoreFile r => r.Path,
        RollbackRecord.RemoveDirectory r => r.Path,
        RollbackRecord.DeleteShortcut r => r.Path,
        RollbackRecord.RestoreRegistryValue r => $"{r.Hive}\\{r.Key}\\{r.Name}",
        RollbackRecord.RestoreRegistryKey r => $"{r.Hive}\\{r.Key}",
        RollbackRecord.RestoreEnv r => $"env:{r.Scope}:{r.Name}",
        RollbackRecord.RestoreDeletedFile r => r.OriginalPath,
        RollbackRecord.RestoreDeletedDirectory r => r.OriginalPath,
        RollbackRecord.RestoreConfigFile r => r.OriginalPath,
        RollbackRecord.RemoveUninstaller r => r.Path,
        RollbackRecord.RemoveService r => r.ServiceName,
        RollbackRecord.DeleteScheduledTask r => r.TaskName,
        RollbackRecord.UnregisterCom r => r.DllPath,
        RollbackRecord.DeleteFirewallRule r => r.RuleName,
        _ => string.Empty,
    };

    /// <summary>
    /// A short, prototype-style reversal line for the interactive uninstall log
    /// (spec T15 / design brief: <c>unlink</c>, <c>path -</c>, <c>reg -</c>,
    /// <c>delete</c>). Derived from the record's declared fields only — no resolved
    /// parameter values, so a secret can never leak into the uninstall log.
    /// </summary>
    private static string DescribeUndo(RollbackRecord record) => record switch
    {
        RollbackRecord.RestoreFile r => $"delete {r.Path}",
        RollbackRecord.RemoveDirectory r => $"rmdir {r.Path}",
        RollbackRecord.DeleteShortcut r => $"unlink {r.Path}",
        RollbackRecord.RestoreRegistryValue r => $"reg - {r.Key}\\{r.Name}",
        RollbackRecord.RestoreRegistryKey r => $"reg - {r.Key}",
        RollbackRecord.RestoreEnv r => r.Name.Equals("PATH", System.StringComparison.OrdinalIgnoreCase)
            ? "path -"
            : $"env - {r.Name}",
        RollbackRecord.RestoreDeletedFile r => $"restore {r.OriginalPath}",
        RollbackRecord.RestoreDeletedDirectory r => $"restore {r.OriginalPath}",
        RollbackRecord.RestoreConfigFile r => $"restore {r.OriginalPath}",
        RollbackRecord.RemoveUninstaller r => $"delete {r.Path}",
        RollbackRecord.DeleteScheduledTask r => $"deltask {r.TaskName}",
        RollbackRecord.UnregisterCom r => $"unregister {r.DllPath}",
        RollbackRecord.DeleteFirewallRule r => $"delfw {r.RuleName}",
        _ => "revert",
    };
}

/// <summary>
/// Why replay anchoring refused a record (R1). A stable code, so a consumer can group,
/// count and route refusals without parsing prose.
/// </summary>
/// <remarks>
/// Part of the cross-lane contract described on <see cref="RefusedRecord"/>. Add members
/// rather than renumbering or repurposing existing ones.
/// </remarks>
public enum ReplayRefusalCode
{
    /// <summary>A path-bearing record targeted something outside the anchored roots.</summary>
    PathOutsideInstallRoots = 1,

    /// <summary>
    /// A registry record targeted a hive, or a subtree, that an installer's rollback may
    /// not reverse at all — outside <c>Software\</c>, or an auto-run / policy /
    /// COM-activation surface.
    /// </summary>
    RegistryOutsideApplicationSpace = 2,

    /// <summary>
    /// The destination was in range but the content the record would restore comes from a
    /// backup or stash outside the anchored roots — the register's "arbitrary file / tree
    /// write from an attacker-chosen stash".
    /// </summary>
    ContentSourceOutsideInstallRoots = 11,

    /// <summary>
    /// A variable the system depends on (<c>PATH</c>, <c>ComSpec</c>, …) may only have
    /// entries removed; the record would have replaced its contents wholesale.
    /// </summary>
    EnvironmentSystemVariableNotReplaceable = 12,

    /// <summary>
    /// The key is an execution mapping (a shell verb, a class registration, a driver map)
    /// and the program it would be restored to is not one this install owns.
    /// </summary>
    ExecutionMappingNotOwned = 3,

    /// <summary>
    /// The key is an execution mapping and the value is not a command line that could be
    /// checked at all.
    /// </summary>
    ExecutionMappingUncheckable = 4,

    /// <summary>
    /// An environment restore would introduce an entry that is neither already present in
    /// the variable nor a directory this install owns.
    /// </summary>
    EnvironmentIntroducesForeignEntry = 5,

    /// <summary>
    /// Deleting a variable the system depends on, whose current contents this install did
    /// not create.
    /// </summary>
    EnvironmentDeleteNotOwned = 6,

    /// <summary>
    /// An environment record could not be verified — the current value was unreadable, or
    /// the host is not Windows.
    /// </summary>
    EnvironmentUnverifiable = 7,

    /// <summary>The named service runs a binary this install did not place.</summary>
    ServiceNotOwned = 8,

    /// <summary>A service record could not be verified on this host.</summary>
    ServiceUnverifiable = 9,

    /// <summary>
    /// A <c>unregister_com</c> record named a DLL that does not resolve inside the install
    /// directory — <c>LoadLibrary</c> plus an export call on an attacker-chosen module.
    /// </summary>
    ComDllOutsideInstallDir = 10,
}

/// <summary>
/// One record that replay anchoring skipped (R1): what it was, what it would have
/// touched, why, and the line that went to the log.
/// </summary>
/// <remarks>
/// <strong>Cross-lane contract.</strong> Lane S5 consumes
/// <see cref="UndoOutcome.RefusedRecords"/> in Stage 2 for register row R15, so this is
/// deliberately structured rather than prose: a consumer must never have to parse
/// <see cref="Message"/> to decide what happened. Treat the shape and the name
/// <c>RefusedRecords</c> as pinned; <see cref="Message"/> is the only part free to change
/// wording.
/// </remarks>
/// <param name="RecordType">
/// The journal record's wire discriminator — <c>restore_file</c>, <c>restore_env</c>,
/// <c>unregister_com</c>, … — matching <c>SerializableRollbackRecord.Type</c>.
/// </param>
/// <param name="Target">
/// The coordinate the record would have acted on, rendered in the natural form for its
/// kind: a filesystem path; <c>HIVE\Key</c> for a registry record; <c>env:scope:NAME</c>
/// for an environment variable; the service name; the DLL path.
/// </param>
/// <param name="Code">The stable reason, for grouping and routing.</param>
/// <param name="Message">
/// The human-readable line reported on the progress sink and written to the <c>/LOG</c>
/// file. For operators, not for parsing.
/// </param>
public sealed record RefusedRecord(
    string RecordType,
    string Target,
    ReplayRefusalCode Code,
    string Message);

/// <summary>
/// Why a record's undo did not achieve what it set out to (R15). A stable code, so a
/// consumer can group, count and route failures without parsing prose — the same
/// contract <see cref="ReplayRefusalCode"/> carries for refusals.
/// </summary>
/// <remarks>
/// Explicitly numbered. <strong>Add members rather than renumbering or repurposing
/// existing ones.</strong>
/// </remarks>
public enum UndoFailureCode
{
    /// <summary>
    /// The record's undo threw. Covers every filesystem / registry / environment record:
    /// a locked file, a denied ACL, a directory where a file was expected.
    /// </summary>
    RecordThrew = 1,

    /// <summary>
    /// <c>sc stop</c> + <c>sc delete</c> ran and the service is still registered — not
    /// even marked for deletion. A Windows service the uninstall left behind, which for
    /// a machine-scope install usually means a SYSTEM-context binary that still starts.
    /// </summary>
    ServiceStillPresent = 2,

    /// <summary>
    /// <c>schtasks /Delete</c> ran and the task is still present. The register's
    /// "permanent SYSTEM scheduled task".
    /// </summary>
    ScheduledTaskStillPresent = 3,

    /// <summary>
    /// <c>netsh advfirewall firewall delete rule</c> ran and the rule still matches —
    /// the register's "open firewall port".
    /// </summary>
    FirewallRuleStillPresent = 4,

    /// <summary>
    /// <c>DllUnregisterServer</c> could not be reached or returned a failure HRESULT, so
    /// the machine-wide COM registration is still live.
    /// </summary>
    ComUnregisterFailed = 5,

    /// <summary>
    /// The external tool the undo needs (<c>sc.exe</c>, <c>schtasks.exe</c>,
    /// <c>netsh.exe</c>) could not be launched, so whether the object was removed is
    /// unknown. Unknown is not success.
    /// </summary>
    ExternalToolUnavailable = 6,
}

/// <summary>
/// One record whose undo ran and did not achieve its goal (R15): what it was, what it
/// was acting on, why, and the line that went to the log.
/// </summary>
/// <remarks>
/// Deliberately the same shape as <see cref="RefusedRecord"/>, and for the same reason:
/// a consumer must never have to parse <see cref="Message"/> to decide what happened.
/// <see cref="Target"/> can be attacker-supplied text (it comes off a journal that was
/// read from disk) and must be rendered as untrusted.
/// </remarks>
/// <param name="RecordType">The journal record's wire discriminator — <c>remove_service</c>, <c>restore_file</c>, …</param>
/// <param name="Target">The coordinate the record acted on, in the same natural form <see cref="RefusedRecord.Target"/> uses.</param>
/// <param name="Code">The stable reason, for grouping and routing.</param>
/// <param name="Message">The human-readable line reported on the progress sink and written to the <c>/LOG</c> file. For operators, not for parsing.</param>
public sealed record FailedRecord(
    string RecordType,
    string Target,
    UndoFailureCode Code,
    string Message);

/// <summary>
/// Thrown by a <see cref="RollbackRecord"/> whose undo ran to completion and left the
/// thing it was meant to remove in place (R15). Distinct from an arbitrary exception so
/// <see cref="RollbackJournal.UndoAsync"/> can attach the record's own
/// <see cref="UndoFailureCode"/> instead of the generic
/// <see cref="UndoFailureCode.RecordThrew"/>.
/// </summary>
public sealed class UndoFailedException : System.Exception
{
    public UndoFailedException()
        : this(UndoFailureCode.RecordThrew, string.Empty, "the undo did not complete")
    {
    }

    public UndoFailedException(string message)
        : this(UndoFailureCode.RecordThrew, string.Empty, message)
    {
    }

    public UndoFailedException(string message, System.Exception? innerException)
        : base(message, innerException)
    {
        Code = UndoFailureCode.RecordThrew;
        Target = string.Empty;
    }

    public UndoFailedException(UndoFailureCode code, string target, string message)
        : base(message)
    {
        Code = code;
        Target = target;
    }

    /// <summary>The stable reason the undo did not achieve its goal.</summary>
    public UndoFailureCode Code { get; }

    /// <summary>The coordinate that is still present (service name, task name, …).</summary>
    public string Target { get; } = string.Empty;
}

/// <summary>
/// The result of a <see cref="RollbackJournal.UndoAsync"/> replay.
/// </summary>
/// <param name="RefusedRecords">
/// One entry per record that replay anchoring skipped (R1). Empty on an unanchored replay
/// and on a clean anchored one. A non-empty list after a legitimate uninstall means either
/// a planted journal or an anchoring bug, and either way it must reach the operator rather
/// than being swallowed. Consumed by lane S5 in Stage 2 for R15 — see
/// <see cref="RefusedRecord"/>.
/// </param>
/// <param name="FailedRecords">
/// One entry per record whose undo ran and did not achieve its goal (R15). Distinct from
/// <paramref name="RefusedRecords"/> in kind: a refusal means the record was never ours
/// to replay, a failure means it was and the replay did not work. Both make
/// <see cref="IsClean"/> false; only a failure is likely to succeed on a retry.
/// </param>
public sealed record UndoOutcome(
    System.Collections.Generic.IReadOnlyList<RefusedRecord> RefusedRecords,
    System.Collections.Generic.IReadOnlyList<FailedRecord> FailedRecords)
{
    /// <summary>
    /// Back-compat overload for callers that predate <paramref name="FailedRecords"/>.
    /// </summary>
    public UndoOutcome(System.Collections.Generic.IReadOnlyList<RefusedRecord> refusedRecords)
        : this(refusedRecords, System.Array.Empty<FailedRecord>())
    {
    }

    /// <summary>
    /// True when every record was replayed and every replay achieved its goal — the only
    /// state in which an uninstall may report success.
    /// </summary>
    public bool IsClean => RefusedRecords.Count == 0 && FailedRecords.Count == 0;
}

public abstract record RollbackRecord
{
    public abstract System.Threading.Tasks.Task UndoAsync(System.Threading.CancellationToken ct);

    public sealed record RestoreFile(string Path, bool ExistedBefore, string? BackupPath) : RollbackRecord
    {
        public override System.Threading.Tasks.Task UndoAsync(System.Threading.CancellationToken ct)
        {
            if (ExistedBefore && BackupPath is not null && System.IO.File.Exists(BackupPath))
            {
                System.IO.File.Copy(BackupPath, Path, overwrite: true);
                System.IO.File.Delete(BackupPath);
            }
            else if (System.IO.File.Exists(Path))
            {
                System.IO.File.Delete(Path);
            }
            return System.Threading.Tasks.Task.CompletedTask;
        }
    }

    public sealed record RemoveDirectory(string Path) : RollbackRecord
    {
        public override System.Threading.Tasks.Task UndoAsync(System.Threading.CancellationToken ct)
        {
            // Only remove if empty — matches the "create only if previously absent" semantics.
            if (System.IO.Directory.Exists(Path) &&
                !System.IO.Directory.EnumerateFileSystemEntries(Path).Any())
            {
                System.IO.Directory.Delete(Path);
            }
            return System.Threading.Tasks.Task.CompletedTask;
        }
    }

    /// <summary>
    /// Delete a shortcut <c>.lnk</c> that the <c>shortcut_create</c> step
    /// materialised. Best-effort — a missing file (already cleaned up by an
    /// earlier failed save) is treated as success.
    /// </summary>
    public sealed record DeleteShortcut(string Path) : RollbackRecord
    {
        public override System.Threading.Tasks.Task UndoAsync(System.Threading.CancellationToken ct)
        {
            if (System.IO.File.Exists(Path))
            {
#pragma warning disable CA1031 // Best-effort undo — failure to delete a stray .lnk should not cascade.
                try
                {
                    System.IO.File.Delete(Path);
                }
                catch
                {
                    // Best-effort; swallow.
                }
#pragma warning restore CA1031
            }
            return System.Threading.Tasks.Task.CompletedTask;
        }
    }

    /// <summary>
    /// Restore a single registry value to its prior state. If the value was
    /// previously absent the rollback deletes whatever the step wrote;
    /// otherwise it re-writes the captured value with its captured kind.
    /// No-op on non-Windows hosts so the type can travel through the
    /// platform-neutral journal API.
    /// </summary>
    public sealed record RestoreRegistryValue(
        string Hive,
        string Key,
        string Name,
        string View,
        string? PriorTypeStr,
        object? PriorValue,
        bool PreviouslyAbsent) : RollbackRecord
    {
        public override System.Threading.Tasks.Task UndoAsync(System.Threading.CancellationToken ct)
        {
            if (!System.OperatingSystem.IsWindows())
            {
                return System.Threading.Tasks.Task.CompletedTask;
            }

            return UndoOnWindows();
        }

        [SupportedOSPlatform("windows")]
        private System.Threading.Tasks.Task UndoOnWindows()
        {
            var hive = RegistryHelper.ParseHive(Hive);
            var view = RegistryHelper.ParseView(View);

            using var baseKey = RegistryKey.OpenBaseKey(hive, view);

            if (PreviouslyAbsent)
            {
                // The value didn't exist before; if the step created it, scrub it.
                using var sub = baseKey.OpenSubKey(Key, writable: true);
                if (sub is null)
                {
                    return System.Threading.Tasks.Task.CompletedTask;
                }
#pragma warning disable CA1031 // Best-effort undo; missing values are fine.
                try { sub.DeleteValue(Name, throwOnMissingValue: false); }
                catch { /* best-effort */ }
#pragma warning restore CA1031
            }
            else if (PriorValue is not null && PriorTypeStr is not null)
            {
                // Re-create the parent key if the step deleted it (delete_value
                // never deletes the key, but delete_key on a parent could).
                using var sub = baseKey.CreateSubKey(Key, writable: true);
                if (sub is null)
                {
                    return System.Threading.Tasks.Task.CompletedTask;
                }
                sub.SetValue(Name, PriorValue, RegistryHelper.ParseValueKind(PriorTypeStr));
            }
            return System.Threading.Tasks.Task.CompletedTask;
        }
    }

    /// <summary>
    /// Restore a Windows environment variable to its prior state. If
    /// <paramref name="PreviouslyAbsent"/> is true the rollback deletes the
    /// value the step wrote; otherwise it re-writes <paramref name="PriorValue"/>
    /// as <c>REG_SZ</c>. After restoration a best-effort
    /// <c>WM_SETTINGCHANGE</c> broadcast notifies running shells of the
    /// reverted state. No-op on non-Windows hosts so the type can travel
    /// through the platform-neutral journal API.
    /// </summary>
    public sealed record RestoreEnv(
        string Scope,
        string Name,
        string? PriorValue,
        bool PreviouslyAbsent) : RollbackRecord
    {
        public override System.Threading.Tasks.Task UndoAsync(System.Threading.CancellationToken ct)
        {
            if (!System.OperatingSystem.IsWindows())
            {
                return System.Threading.Tasks.Task.CompletedTask;
            }
            UndoOnWindows();
            return System.Threading.Tasks.Task.CompletedTask;
        }

        [SupportedOSPlatform("windows")]
        private void UndoOnWindows()
        {
            using var key = OpenEnvKey(Scope, writable: true);
            if (key is null)
            {
                return;
            }

            if (PreviouslyAbsent)
            {
#pragma warning disable CA1031 // Best-effort undo: a missing value is fine.
                try { key.DeleteValue(Name, throwOnMissingValue: false); }
                catch { /* best-effort */ }
#pragma warning restore CA1031
            }
            else if (PriorValue is not null)
            {
                key.SetValue(Name, PriorValue, RegistryValueKind.String);
            }

            EnvBroadcast.NotifySettingChange();
        }

        [SupportedOSPlatform("windows")]
        private static RegistryKey? OpenEnvKey(string scope, bool writable) => scope switch
        {
            "user" => Registry.CurrentUser.OpenSubKey("Environment", writable),
            "machine" => Registry.LocalMachine.OpenSubKey(
                @"System\CurrentControlSet\Control\Session Manager\Environment", writable),
            _ => throw new System.ArgumentException($"unknown env scope '{scope}'"),
        };
    }

    /// <summary>
    /// Restore a registry key whose immediate values were captured before
    /// deletion. Recursive subtree restore is an acknowledged gap — only
    /// the values directly under the key are re-created. If the key was
    /// previously absent the rollback is a no-op.
    /// </summary>
    public sealed record RestoreRegistryKey(
        string Hive,
        string Key,
        string View,
        System.Collections.Generic.IReadOnlyList<RegistryValueSnapshot> ValuesAtKeyLevel,
        bool PreviouslyAbsent) : RollbackRecord
    {
        public override System.Threading.Tasks.Task UndoAsync(System.Threading.CancellationToken ct)
        {
            if (!System.OperatingSystem.IsWindows())
            {
                return System.Threading.Tasks.Task.CompletedTask;
            }
            if (PreviouslyAbsent)
            {
                return System.Threading.Tasks.Task.CompletedTask;
            }
            return UndoOnWindows();
        }

        [SupportedOSPlatform("windows")]
        private System.Threading.Tasks.Task UndoOnWindows()
        {
            var hive = RegistryHelper.ParseHive(Hive);
            var view = RegistryHelper.ParseView(View);
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var sub = baseKey.CreateSubKey(Key, writable: true);
            if (sub is null)
            {
                return System.Threading.Tasks.Task.CompletedTask;
            }
            foreach (var snap in ValuesAtKeyLevel)
            {
                if (snap.Value is null)
                {
                    continue;
                }
                sub.SetValue(snap.Name, snap.Value, RegistryHelper.ParseValueKind(snap.TypeStr));
            }
            return System.Threading.Tasks.Task.CompletedTask;
        }
    }

    /// <summary>
    /// Restores a file that was deleted by <c>file_delete</c>. The bytes were
    /// stashed to a temp path before deletion; rollback copies them back.
    /// If the stash is gone (already cleaned up) the record is a no-op.
    /// </summary>
    public sealed record RestoreDeletedFile(string OriginalPath, string StashPath) : RollbackRecord
    {
        public override System.Threading.Tasks.Task UndoAsync(System.Threading.CancellationToken ct)
        {
            if (System.IO.File.Exists(StashPath))
            {
                System.IO.Directory.CreateDirectory(
                    System.IO.Path.GetDirectoryName(OriginalPath)!);
                System.IO.File.Copy(StashPath, OriginalPath, overwrite: true);
#pragma warning disable CA1031 // Best-effort stash cleanup; a leftover temp file is harmless.
                try { System.IO.File.Delete(StashPath); }
                catch { /* best-effort */ }
#pragma warning restore CA1031
            }
            return System.Threading.Tasks.Task.CompletedTask;
        }
    }

    /// <summary>
    /// Restores a directory subtree that was deleted by <c>directory_delete</c>.
    /// The subtree was copied to <paramref name="StashPath"/> before deletion;
    /// rollback moves it back recursively. If the stash is gone the record is
    /// a no-op.
    /// </summary>
    public sealed record RestoreDeletedDirectory(string OriginalPath, string StashPath) : RollbackRecord
    {
        public override System.Threading.Tasks.Task UndoAsync(System.Threading.CancellationToken ct)
        {
            if (!System.IO.Directory.Exists(StashPath))
            {
                return System.Threading.Tasks.Task.CompletedTask;
            }

            System.IO.Directory.CreateDirectory(OriginalPath);
            CopyDirectoryRecursive(StashPath, OriginalPath);
#pragma warning disable CA1031 // Best-effort stash cleanup.
            try { System.IO.Directory.Delete(StashPath, recursive: true); }
            catch { /* best-effort */ }
#pragma warning restore CA1031
            return System.Threading.Tasks.Task.CompletedTask;
        }

        private static void CopyDirectoryRecursive(string source, string destination)
        {
            foreach (var file in System.IO.Directory.EnumerateFiles(source, "*", System.IO.SearchOption.TopDirectoryOnly))
            {
                var rel = System.IO.Path.GetFileName(file);
                System.IO.File.Copy(file, System.IO.Path.Combine(destination, rel), overwrite: true);
            }
            foreach (var dir in System.IO.Directory.EnumerateDirectories(source, "*", System.IO.SearchOption.TopDirectoryOnly))
            {
                var rel = System.IO.Path.GetFileName(dir);
                var destSub = System.IO.Path.Combine(destination, rel);
                System.IO.Directory.CreateDirectory(destSub);
                CopyDirectoryRecursive(dir, destSub);
            }
        }
    }

    /// <summary>
    /// Restores a config file (P8 <c>ini_write</c> / <c>json_edit</c> /
    /// <c>xml_edit</c>) to its exact pre-edit state on a mid-install rollback. When
    /// the file existed before the edit, its whole content was stashed to
    /// <paramref name="StashPath"/> and rollback copies it back byte-for-byte; when
    /// the file did NOT exist (a <c>create_if_missing</c> edit), <paramref name="StashPath"/>
    /// is <c>null</c> and rollback deletes the file the edit created. The stash is
    /// reclaimed by <see cref="DiscardTransientStashes"/> on a successful install,
    /// so a committed config edit is not reverted at uninstall time.
    /// </summary>
    public sealed record RestoreConfigFile(string OriginalPath, string? StashPath) : RollbackRecord
    {
        public override System.Threading.Tasks.Task UndoAsync(System.Threading.CancellationToken ct)
        {
            if (StashPath is not null && System.IO.File.Exists(StashPath))
            {
                var dir = System.IO.Path.GetDirectoryName(OriginalPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    System.IO.Directory.CreateDirectory(dir);
                }
                System.IO.File.Copy(StashPath, OriginalPath, overwrite: true);
#pragma warning disable CA1031 // Best-effort stash cleanup; a leftover temp file is harmless.
                try { System.IO.File.Delete(StashPath); }
                catch { /* best-effort */ }
#pragma warning restore CA1031
            }
            else if (StashPath is null && System.IO.File.Exists(OriginalPath))
            {
                // The edit created this file; undo removes it.
#pragma warning disable CA1031 // Best-effort undo; a leftover created file is preferable to a crash.
                try { System.IO.File.Delete(OriginalPath); }
                catch { /* best-effort */ }
#pragma warning restore CA1031
            }
            return System.Threading.Tasks.Task.CompletedTask;
        }
    }

    /// <summary>
    /// Removes the survivable <c>uninstall.exe</c> copied into the install dir as
    /// the final install step (spec T15). Undo is delegated to
    /// <see cref="SelfDelete"/>, which tolerates the case where <see cref="Path"/>
    /// is the <em>running</em> uninstaller image: it cannot delete its own live
    /// image, so it schedules reboot-time deletion instead. Journal replay never
    /// aborts on this entry — the delete is best-effort and never throws.
    /// </summary>
    public sealed record RemoveUninstaller(string Path) : RollbackRecord
    {
        public override System.Threading.Tasks.Task UndoAsync(System.Threading.CancellationToken ct)
        {
            SelfDelete.Remove(Path);
            return System.Threading.Tasks.Task.CompletedTask;
        }
    }

    /// <summary>
    /// Stops + deletes a Windows service created by <c>service_install</c>.
    /// Recorded BEFORE the create so an interrupted install can still unwind.
    /// On uninstall this runs as part of the journal replay.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The contract is the END STATE, not the exit code (R15).</strong> "No
    /// service after rollback" is still the goal, so a service that does not exist is a
    /// success even though <c>sc delete</c> exits 1060 for it — reporting that as a
    /// failure would turn every uninstall of an app whose service a user already removed
    /// into a permanently "failed" one. What is no longer tolerated is the opposite: the
    /// service is <em>still registered</em> after both commands ran, which for a
    /// machine-scope install means a SYSTEM-context binary that still starts, left behind
    /// by an uninstall that reported success and then deleted the only record that could
    /// have removed it.
    /// </para>
    /// <para>
    /// A service marked for deletion (<c>DeleteFlag</c>) counts as removed: the SCM
    /// completes it when the last handle closes, and refusing there would fail every
    /// uninstall of a service that was running.
    /// </para>
    /// </remarks>
    public sealed record RemoveService(string ServiceName) : RollbackRecord
    {
        public override async System.Threading.Tasks.Task UndoAsync(System.Threading.CancellationToken ct)
        {
            if (!System.OperatingSystem.IsWindows())
            {
                // There is no SCM to leave residue in, so there is nothing to fail at.
                return;
            }

            await ExternalUndoTool.RunAsync("sc.exe", new[] { "stop", ServiceName }, ct)
                .ConfigureAwait(false);
            var deleteExit = await ExternalUndoTool
                .RunAsync("sc.exe", new[] { "delete", ServiceName }, ct)
                .ConfigureAwait(false);

            // ERROR_SERVICE_DOES_NOT_EXIST — the goal already holds.
            if (deleteExit is 0 or 1060)
            {
                return;
            }

            var residue = ServiceResidue(ServiceName);
            if (residue is ServiceResidueState.Gone or ServiceResidueState.PendingDeletion)
            {
                return;
            }

            if (deleteExit is null)
            {
                throw new UndoFailedException(
                    UndoFailureCode.ExternalToolUnavailable,
                    ServiceName,
                    $"remove_service: sc.exe could not be run, so whether service '{ServiceName}' " +
                    "was removed is unknown — treating unknown as not removed");
            }

            throw new UndoFailedException(
                UndoFailureCode.ServiceStillPresent,
                ServiceName,
                $"remove_service: service '{ServiceName}' is still registered after " +
                $"sc delete (exit {deleteExit}) — it was NOT removed");
        }

        private enum ServiceResidueState
        {
            Gone,
            PendingDeletion,
            Present,
            Unknown,
        }

        private static ServiceResidueState ServiceResidue(string serviceName)
        {
            if (!System.OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(serviceName))
            {
                return ServiceResidueState.Unknown;
            }
            return ReadServiceResidue(serviceName);
        }

        [SupportedOSPlatform("windows")]
        private static ServiceResidueState ReadServiceResidue(string serviceName)
        {
#pragma warning disable CA1031 // An unreadable SCM key cannot confirm removal; say so rather than assuming either way.
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    $@"System\CurrentControlSet\Services\{serviceName}", writable: false);
                if (key is null)
                {
                    return ServiceResidueState.Gone;
                }
                // The SCM sets DeleteFlag when `sc delete` reached a running service; the
                // removal completes when the last handle closes.
                if (key.GetValue("DeleteFlag") is int flag && flag != 0)
                {
                    return ServiceResidueState.PendingDeletion;
                }
                return ServiceResidueState.Present;
            }
            catch
            {
                return ServiceResidueState.Unknown;
            }
#pragma warning restore CA1031
        }
    }

    /// <summary>
    /// Deletes a Windows Scheduled Task created by <c>scheduled_task_create</c>
    /// (P11, T11.1). Recorded BEFORE the create so an interrupted install can
    /// still unwind. Mirrors <see cref="RemoveService"/>: the contract is the END
    /// STATE — "no task after rollback" — not the exit code. <c>schtasks /Delete</c>
    /// on a missing task exits non-zero and that is a SUCCESS; a task still present
    /// after the delete is the failure (R15's "permanent SYSTEM scheduled task").
    /// Only the task NAME is carried — no secrets, no resolved program path.
    /// </summary>
    public sealed record DeleteScheduledTask(string TaskName) : RollbackRecord
    {
        public override async System.Threading.Tasks.Task UndoAsync(System.Threading.CancellationToken ct)
        {
            if (!System.OperatingSystem.IsWindows())
            {
                return;
            }

            var deleteExit = await ExternalUndoTool
                .RunAsync("schtasks.exe", new[] { "/Delete", "/TN", TaskName, "/F" }, ct)
                .ConfigureAwait(false);
            if (deleteExit == 0)
            {
                return;
            }
            if (deleteExit is null)
            {
                throw new UndoFailedException(
                    UndoFailureCode.ExternalToolUnavailable,
                    TaskName,
                    "delete_scheduled_task: schtasks.exe could not be run, so whether task " +
                    $"'{TaskName}' was removed is unknown — treating unknown as not removed");
            }

            // Non-zero covers BOTH "no such task" (the goal already holds) and "access
            // denied" (it emphatically does not). Only a query separates them.
            var queryExit = await ExternalUndoTool
                .RunAsync("schtasks.exe", new[] { "/Query", "/TN", TaskName }, ct)
                .ConfigureAwait(false);
            if (queryExit != 0)
            {
                return;
            }

            throw new UndoFailedException(
                UndoFailureCode.ScheduledTaskStillPresent,
                TaskName,
                $"delete_scheduled_task: scheduled task '{TaskName}' still exists after " +
                $"schtasks /Delete (exit {deleteExit}) — it was NOT removed");
        }
    }

    /// <summary>
    /// Unregisters a COM DLL registered by <c>com_register</c> (P11, T11.2) by
    /// invoking its exported <c>DllUnregisterServer</c> through the same native
    /// path the register used (see
    /// <see cref="SigilBuild.Wrapper.Steps.Win32.ComRegistration"/>). Recorded
    /// BEFORE the register so an interrupted install can still unwind. Only the DLL
    /// PATH is carried — no secrets, no registry contents.
    /// <para>
    /// <strong>The outcome is no longer ignored (R15).</strong> The goal is "COM
    /// registration gone after rollback", and unlike a missing service there is no
    /// benign reading of a failed <c>DllUnregisterServer</c>: the export is missing, the
    /// module will not load, or it returned a failure HRESULT — in all three the
    /// machine-wide registration the install created is still live, and reporting the
    /// uninstall as a success would delete the only record that could have removed it.
    /// The record is replayed LIFO <em>before</em> the file records that delete the DLL,
    /// so "the DLL is already gone" is not a case this has to tolerate.
    /// </para>
    /// </summary>
    public sealed record UnregisterCom(string DllPath) : RollbackRecord
    {
        public override System.Threading.Tasks.Task UndoAsync(System.Threading.CancellationToken ct)
        {
            if (!System.OperatingSystem.IsWindows())
            {
                return System.Threading.Tasks.Task.CompletedTask;
            }

            // FreeLibrary is handled inside Invoke; the expected COM failure modes come
            // back as an outcome rather than an exception.
            var result = SigilBuild.Wrapper.Steps.Win32.ComRegistration.Invoke(
                DllPath, "DllUnregisterServer");
            if (result.Outcome == SigilBuild.Wrapper.Steps.Win32.ComRegistration.ComExportOutcome.Ok)
            {
                return System.Threading.Tasks.Task.CompletedTask;
            }

            throw new UndoFailedException(
                UndoFailureCode.ComUnregisterFailed,
                DllPath,
                $"unregister_com: DllUnregisterServer on '{DllPath}' did not succeed " +
                $"({result.Outcome}, win32 {result.Win32Error}, hr 0x{result.HResult:X8}) — " +
                "the COM registration is still in place");
        }
    }

    /// <summary>
    /// Deletes a Windows Defender Firewall rule created by <c>firewall_rule</c>
    /// (P11, T11.3) via <c>netsh advfirewall firewall delete rule</c>. Recorded
    /// BEFORE the add so an interrupted install can still unwind. Mirrors
    /// <see cref="RemoveService"/>/<see cref="DeleteScheduledTask"/>: the contract is
    /// the END STATE — "no rule after rollback". netsh's "No rules match the specified
    /// criteria" is a SUCCESS (the rule was never created, or is already gone); a rule
    /// that still matches after the delete is the failure (R15's "open firewall port").
    /// Only the rule NAME is carried — no secrets, no resolved program path.
    /// </summary>
    public sealed record DeleteFirewallRule(string RuleName) : RollbackRecord
    {
        public override async System.Threading.Tasks.Task UndoAsync(System.Threading.CancellationToken ct)
        {
            if (!System.OperatingSystem.IsWindows())
            {
                return;
            }

            var deleteExit = await ExternalUndoTool.RunAsync(
                "netsh.exe",
                new[] { "advfirewall", "firewall", "delete", "rule", $"name={RuleName}" },
                ct).ConfigureAwait(false);
            if (deleteExit == 0)
            {
                return;
            }
            if (deleteExit is null)
            {
                throw new UndoFailedException(
                    UndoFailureCode.ExternalToolUnavailable,
                    RuleName,
                    "firewall_rule: netsh.exe could not be run, so whether rule " +
                    $"'{RuleName}' was removed is unknown — treating unknown as not removed");
            }

            // Non-zero is "no rules match" in the common case and a denial in the case
            // that matters. Ask.
            var queryExit = await ExternalUndoTool.RunAsync(
                "netsh.exe",
                new[] { "advfirewall", "firewall", "show", "rule", $"name={RuleName}" },
                ct).ConfigureAwait(false);
            if (queryExit != 0)
            {
                return;
            }

            throw new UndoFailedException(
                UndoFailureCode.FirewallRuleStillPresent,
                RuleName,
                $"firewall_rule: rule '{RuleName}' still matches after netsh delete " +
                $"(exit {deleteExit}) — it was NOT removed");
        }
    }
}

/// <summary>
/// Runs one of the three external undo tools (<c>sc.exe</c>, <c>schtasks.exe</c>,
/// <c>netsh.exe</c>) and returns its exit code, or <c>null</c> when the process could
/// not be started at all.
/// </summary>
/// <remarks>
/// The <c>null</c> is the point (R15): "could not run the tool" and "the tool ran and
/// said the object is gone" used to be the same outcome — a swallowed exception — and
/// only one of them means the uninstall achieved anything. stdout/stderr are redirected
/// so a chatty tool never blocks on a full pipe, and read to completion for the same
/// reason.
/// </remarks>
internal static class ExternalUndoTool
{
    internal static async System.Threading.Tasks.Task<int?> RunAsync(
        string exe,
        string[] args,
        System.Threading.CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        System.Diagnostics.Process? proc;
        try
        {
            proc = System.Diagnostics.Process.Start(psi);
        }
#pragma warning disable CA1031 // Any spawn failure is the same answer: we do not know what happened.
        catch (System.Exception)
        {
            return null;
        }
#pragma warning restore CA1031

        if (proc is null)
        {
            return null;
        }

        using (proc)
        {
            // Drain both pipes so a verbose tool cannot deadlock the wait.
            var stdout = proc.StandardOutput.ReadToEndAsync(ct);
            var stderr = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            _ = await stdout.ConfigureAwait(false);
            _ = await stderr.ConfigureAwait(false);
            return proc.ExitCode;
        }
    }
}

/// <summary>
/// Captured state of a single registry value at the moment it was
/// snapshotted by <see cref="RollbackRecord.RestoreRegistryKey"/>. Held
/// outside the record's parameter list to keep the public API readable.
/// </summary>
public readonly record struct RegistryValueSnapshot(string Name, string TypeStr, object? Value);
