namespace SigilBuild.Wrapper.Engine;

using System;
using System.IO;
using System.Text.Json;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Json;

/// <summary>
/// Reads and writes the install-state JSON at
/// <c>&lt;StateRoot&gt;\Sigil\&lt;AppId&gt;\uninstall.json</c>, where
/// <c>StateRoot</c> is <c>%ProgramData%</c> for a per-machine install and
/// <c>%LocalAppData%</c> for a per-user install (T12). After a successful install
/// the engine snapshots its <see cref="RollbackJournal"/> here (recording the
/// scope), then on a later <c>/Uninstall</c> invocation <c>UninstallEngine</c>
/// rehydrates and replays it in reverse in the same scope.
/// </summary>
/// <remarks>
/// <para>
/// The on-disk schema is the AOT-source-generated
/// <see cref="SerializableRollbackJournal"/>; do not switch to ad-hoc
/// reflection serializers without first updating
/// <see cref="WrapperBlobJsonContext"/>.
/// </para>
/// <para>
/// Saves overwrite the prior file unconditionally — a re-installation
/// silently replaces any stale state.
/// </para>
/// </remarks>
internal static class UninstallStateStore
{
    /// <summary>
    /// R19: <c>uninstall.json</c> is attacker-supplied bytes until it has been read
    /// and validated, and the read materializes the whole file. Cap it <em>before</em>
    /// the read, not after. A real journal is one short record per mutation an
    /// installer made — a few hundred records of a few dozen bytes each — so 4 MB is
    /// three orders of magnitude of headroom and still an instant read. Anything
    /// larger is not a Sigil journal.
    /// </summary>
    private const long MaxStateFileBytes = 4L * 1024 * 1024;

    /// <summary>
    /// R19: the size cap alone does not bound the work, because the records are tiny
    /// — 4 MB of <c>[null,null,…]</c> is on the order of a million of them. A real
    /// install journals one record per file, registry value, shortcut and PATH edit,
    /// so 50,000 is far beyond any plausible installer while still bounding
    /// rehydration and the replay that follows it.
    /// </summary>
    private const int MaxStateRecords = 50_000;

    /// <summary>
    /// Per-app state directory: <c>&lt;StateRoot&gt;\Sigil\&lt;AppId&gt;</c>
    /// (<c>%ProgramData%</c> for machine scope, <c>%LocalAppData%</c> for user).
    /// </summary>
    public static string DirectoryFor(string appId, InstallScope scope)
    {
        ArgumentException.ThrowIfNullOrEmpty(appId);
        return Path.Combine(ScopeLayout.For(scope).StateRoot, "Sigil", appId);
    }

    /// <summary>Full path to <c>uninstall.json</c> for the given app id + scope.</summary>
    public static string PathFor(string appId, InstallScope scope) =>
        Path.Combine(DirectoryFor(appId, scope), "uninstall.json");

    /// <summary>
    /// The state loaded from disk: the rehydrated <paramref name="Journal"/> and
    /// the <paramref name="Scope"/> the install was recorded under (which drives
    /// ARP-hive and state-dir selection on uninstall).
    /// </summary>
    /// <param name="InstallDir">
    /// The directory the install actually landed in, as recorded at save time, or
    /// <c>null</c> for state written before the field existed. This — not a recomputed
    /// default — is what the caller anchors the replay to (R1 clause (c)); a wizard- or
    /// <c>/D=</c>-chosen destination is not recoverable any other way at uninstall time.
    /// </param>
    public sealed record LoadedState(
        RollbackJournal Journal, InstallScope Scope, string? InstallDir);

    /// <summary>
    /// The outcome of a load attempt. <paramref name="State"/> is the rehydrated
    /// state, or <c>null</c>. <paramref name="RefusalReason"/> is non-<c>null</c>
    /// only when state was found but <em>refused</em> on provenance grounds (R1) —
    /// which is emphatically not the same thing as "no prior install", and callers
    /// must not report it as such.
    /// </summary>
    public sealed record LoadAttempt(LoadedState? State, string? RefusalReason);

    /// <summary>
    /// Persist <paramref name="journal"/> as <c>uninstall.json</c> under the
    /// scope-correct per-app state directory, creating it if needed, and record
    /// <paramref name="scope"/> in the file (T12). <paramref name="progress"/>
    /// carries the R1 hardening trail (e.g. a repaired state-directory DACL) into
    /// the console / wizard log / <c>/LOG</c> file; the store has no logger of its own.
    /// <paramref name="installDir"/> is the directory the install actually landed in and
    /// is recorded so the uninstall can anchor its replay to it (R1 clause (c)).
    /// </summary>
    public static void Save(
        string appId,
        RollbackJournal journal,
        InstallScope scope,
        System.Collections.Generic.IReadOnlyList<string>? secretValues = null,
        IProgress<StepProgress>? progress = null,
        string? installDir = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(appId);
        ArgumentNullException.ThrowIfNull(journal);

        var dir = DirectoryFor(appId, scope);

        // R1: machine scope lands in %ProgramData%, whose inherited DACL grants
        // BUILTIN\Users write and makes the creating user CREATOR OWNER. Create it
        // with an explicit, non-inherited DACL instead — and re-apply that DACL to a
        // directory that already exists without it, which is the state every machine
        // with a pre-fix install is in. User scope legitimately lives in the user's
        // own profile, so hardening it would be meaningless.
        if (scope == InstallScope.Machine && OperatingSystem.IsWindows())
        {
            StateDirectorySecurity.CreateHardened(dir, progress);
        }
        else
        {
            Directory.CreateDirectory(dir);
        }

        var records = new SerializableRollbackRecord[journal.Records.Count];
        for (var i = 0; i < journal.Records.Count; i++)
        {
            records[i] = journal.Records[i].ToSerializable();
        }

        var serializable = new SerializableRollbackJournal
        {
            AppId = appId,
            Version = "1",
            Scope = scope,
            InstallDir = string.IsNullOrWhiteSpace(installDir) ? null : installDir,
            Records = records,
        };

        var json = JsonSerializer.Serialize(
            serializable,
            WrapperBlobJsonContext.Default.SerializableRollbackJournal);

        // Secret hygiene (decision 6): a Secret parameter value must never reach
        // persisted uninstall state. The journal captures *prior* system state, so
        // a freshly-written secret normally cannot land here — but redact any
        // literal secret occurrence defensively before the file touches disk.
        json = RedactSecrets(json, secretValues);

        WriteReplacingAnyExistingFile(PathFor(appId, scope), dir, json);
    }

    /// <summary>
    /// Write <paramref name="json"/> to <paramref name="target"/> by creating a fresh
    /// file in <paramref name="directory"/> and moving it over the target, never by
    /// truncating the target in place.
    /// </summary>
    /// <remarks>
    /// <para>
    /// R1, file half: <see cref="File.WriteAllText(string, string?)"/> truncates an
    /// existing file <em>in place</em>. It does not recreate it, so the file keeps its
    /// original owner and its original explicit ACEs across the write. An unprivileged
    /// user who pre-creates <c>uninstall.json</c> therefore still owns it after an
    /// elevated install has written to it, still holds implicit <c>WRITE_DAC</c>, and
    /// can re-grant themselves write and rewrite the records the elevated uninstall
    /// later replays — even though <see cref="StateDirectorySecurity.CreateHardened"/>
    /// hardened the directory around it.
    /// </para>
    /// <para>
    /// A brand-new file in the (already hardened) directory inherits that directory's
    /// admin-only DACL and is owned by whoever wrote it, so nothing of the planted
    /// file's security descriptor survives. <see cref="File.Move(string, string, bool)"/>
    /// maps to <c>MoveFileEx</c> with <c>MOVEFILE_REPLACE_EXISTING</c>, which replaces
    /// the destination wholesale — deliberately <em>not</em>
    /// <see cref="File.Replace(string, string, string?)"/>, whose documented purpose is
    /// to preserve the destination's attributes and ACL, i.e. exactly the bug.
    /// </para>
    /// <para>
    /// Chosen over delete-then-create because the swap is atomic: a crash leaves either
    /// the complete old state or the complete new state, never a hardened directory with
    /// no <c>uninstall.json</c> at all — which would read as "no prior install" and
    /// leave the app unremovable. The staging file is cleaned up on every failure path.
    /// </para>
    /// </remarks>
    private static void WriteReplacingAnyExistingFile(string target, string directory, string json)
    {
        // Same directory as the target, so the move is a rename on one volume (atomic)
        // and the staging file inherits the hardened DACL from the moment it exists.
        var staging = Path.Combine(directory, $"uninstall.json.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(staging, json);
            File.Move(staging, target, overwrite: true);
        }
        finally
        {
            // Only reachable when the write or the move failed — a successful move
            // consumes the staging file.
            if (File.Exists(staging))
            {
#pragma warning disable CA1031 // Best-effort cleanup; the original failure is what the caller must see.
                try
                {
                    File.Delete(staging);
                }
                catch
                {
                    // Best-effort.
                }
#pragma warning restore CA1031
            }
        }
    }

    private static string RedactSecrets(
        string json, System.Collections.Generic.IReadOnlyList<string>? secretValues)
    {
        if (secretValues is null || secretValues.Count == 0)
        {
            return json;
        }
        foreach (var secret in secretValues)
        {
            if (!string.IsNullOrEmpty(secret))
            {
                json = json.Replace(secret, "***", StringComparison.Ordinal);
            }
        }
        return json;
    }

    /// <summary>
    /// Load and rehydrate the persisted journal for <paramref name="appId"/>,
    /// from <paramref name="preferredScope"/>'s directory and <em>only</em> that
    /// directory (R1). <paramref name="preferredScope"/> is resolved from the
    /// uninstall command line (e.g. the <c>/allusers</c> ARP <c>UninstallString</c>);
    /// there is deliberately no fall-through to the opposite scope, because that let a
    /// machine-scope operation read <c>%LocalAppData%</c>. The returned
    /// <see cref="LoadedState.Scope"/> is the scope of the DIRECTORY the file was found
    /// in — never the <c>scope</c> field inside the file — and drives ARP-hive /
    /// state-dir selection. Returns <c>null</c> when no
    /// state file exists in that scope; the caller
    /// (<c>UninstallEngine</c>) translates that into the documented "no uninstall
    /// state found" error. A file that exists but is oversized, malformed, over the
    /// record ceiling or un-rehydratable is a <em>refusal</em>, not an absence (R19).
    /// Machine-scope state is refused outright (R1) unless BOTH
    /// its directory (<see cref="StateDirectorySecurity.IsTrusted"/>) and the
    /// <c>uninstall.json</c> file itself
    /// (<see cref="StateDirectorySecurity.IsTrustedFile"/>) pass the provenance check,
    /// and the refusal is reported on <paramref name="progress"/> — the store has no
    /// logger of its own, and the caller's progress sink is what the <c>/LOG</c> file
    /// is fed from. A missing state file is an <em>absence</em>, never a refusal, so a
    /// first install is unaffected.
    /// Use <see cref="Load"/> when the caller must tell a refusal apart from an
    /// absence; this overload collapses both to <c>null</c>.
    /// </summary>
    public static LoadedState? TryLoad(
        string appId,
        InstallScope preferredScope,
        IProgress<StepProgress>? progress = null)
        => Load(appId, preferredScope, progress).State;

    /// <summary>
    /// <see cref="TryLoad"/> with the refusal distinguished from the absence: a
    /// non-<c>null</c> <see cref="LoadAttempt.RefusalReason"/> means state WAS present
    /// and was rejected — on R1 provenance grounds, or on R19 readability grounds
    /// (size ceiling, malformed JSON, record ceiling, un-rehydratable record).
    /// Reporting either as "no uninstall state found" would tell the operator the
    /// opposite of what happened and would mask an attack, so
    /// <c>UninstallEngine</c> consumes this shape.
    /// </summary>
    public static LoadAttempt Load(
        string appId,
        InstallScope preferredScope,
        IProgress<StepProgress>? progress = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(appId);

        // R1 clause (b): ONE scope, and only the requested one. This used to search
        // the preferred scope and then fall through to the OPPOSITE scope, so an
        // elevated /allusers uninstall read %LocalAppData% — a directory the
        // unprivileged user owns outright — whenever %ProgramData% held no state,
        // which is the normal case on a machine that never had a machine-scope
        // install. Crossing the boundary is the bug; convenience is not a reason to
        // reintroduce it. A machine uninstall that finds nothing must report "no
        // state", not silently reach into the user's profile.
        var dirScope = preferredScope == InstallScope.Machine
            ? InstallScope.Machine
            : InstallScope.User;

        var path = PathFor(appId, dirScope);
        if (!File.Exists(path))
        {
            return new LoadAttempt(null, null);
        }

        // R1: an unprivileged user can pre-create %ProgramData%\Sigil\<AppId>
        // and become CREATOR OWNER of the file the elevated uninstall later
        // replays. Refuse rather than replay, and say so.
        if (dirScope == InstallScope.Machine && OperatingSystem.IsWindows())
        {
            var dir = DirectoryFor(appId, dirScope);

            // BOTH objects must be trusted. The directory alone is not enough:
            // File.WriteAllText truncates in place, so a pre-created uninstall.json
            // keeps its attacker owner and ACEs even inside a hardened directory
            // (see UninstallStateStore.WriteReplacingAnyExistingFile). The file
            // alone is not enough either: a writable directory lets an attacker
            // swap the file wholesale.
            var directoryTrusted = StateDirectorySecurity.IsTrusted(dir);
            var fileTrusted = StateDirectorySecurity.IsTrustedFile(path);

            if (!directoryTrusted || !fileTrusted)
            {
                var what = !directoryTrusted && !fileTrusted
                    ? "the state directory and the state file"
                    : !directoryTrusted ? "the state directory" : "the state file";
                var reason =
                    $"refusing state in '{dir}': {what} failed the provenance check " +
                    "(the owner must be SYSTEM, Administrators or TrustedInstaller, and no " +
                    "non-administrator may hold a write-class right), so an unprivileged " +
                    "user could have authored — or could still alter — the records";
                progress?.Report(new StepProgress(0, 0, reason, IsError: true));
                return new LoadAttempt(null, reason);
            }
        }

        // R19: read, deserialize AND rehydrate under ONE try. Rehydration used to sit
        // outside it, and it throws on an unknown discriminator, a null array element
        // or a missing required field (SerializableRollbackRecord.ToRollbackRecord) —
        // none of which the deserialize-only catch covered, and which nothing above
        // (UninstallEngine.RunAsync, InstallSession) caught either. A one-line planted
        // file therefore killed every install AND every uninstall of that AppId with
        // an unhandled exception: a persistent, per-app denial of service that any
        // user who can write the state directory could arm. Failing closed here is
        // right; failing fatally is not.
        SerializableRollbackJournal? s;
        RollbackJournal journal;
        try
        {
            // Bounded BEFORE the read, so an oversized file is never materialized.
            var length = new FileInfo(path).Length;
            if (length > MaxStateFileBytes)
            {
                return Unreadable(
                    path,
                    $"the state file is {length} bytes, over the {MaxStateFileBytes}-byte ceiling",
                    progress);
            }

            s = JsonSerializer.Deserialize(
                File.ReadAllText(path),
                WrapperBlobJsonContext.Default.SerializableRollbackJournal);

            if (s is null)
            {
                return Unreadable(path, "the state file deserialized to nothing", progress);
            }

            // A literal `"records": null` binds null over the property initializer.
            var records = s.Records;
            if (records is null)
            {
                return Unreadable(path, "the state file carries no records array", progress);
            }

            if (records.Length > MaxStateRecords)
            {
                return Unreadable(
                    path,
                    $"the state file declares {records.Length} records, over the " +
                    $"{MaxStateRecords}-record ceiling",
                    progress);
            }

            journal = new RollbackJournal();
            foreach (var rec in records)
            {
                journal.Append(rec.ToRollbackRecord());
            }
        }
#pragma warning disable CA1031 // The point of R19: ANY failure here is "state unreadable", never an escape.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Unreadable(path, $"the state file could not be read ({ex.Message})", progress);
        }
#pragma warning restore CA1031

        // The scope is the DIRECTORY's scope, deliberately — never s.Scope.
        //
        // R1 clause (b): the serialized `scope` field is attacker-controlled data. It
        // drove the ARP hive (HKLM vs HKCU) and the state directory that the uninstall
        // then wrote to and deleted, so a user-scope file claiming "machine" made an
        // unprivileged uninstall operate on machine-wide state. A value read out of the
        // object whose trustworthiness is in question can never decide the privilege
        // that object is handled with. The field stays on the wire DTO for backward
        // compatibility with state written before this fix — reading it back must never
        // be reintroduced.
        return new LoadAttempt(new LoadedState(journal, dirScope, s.InstallDir), null);
    }

    /// <summary>
    /// R19: state that is PRESENT but cannot be read as a journal — oversized,
    /// malformed, over the record ceiling, or carrying a record that will not
    /// rehydrate. Reported as a <em>refusal</em> on the same channel as the R1
    /// provenance refusal, deliberately: both mean "there is a file here and it was
    /// not replayed", and collapsing that into the absence channel would print "no
    /// uninstall state found" for a file the operator can see on disk — the same
    /// misreport R1 closed, and the same cover for an attacker.
    /// </summary>
    private static LoadAttempt Unreadable(
        string path, string detail, IProgress<StepProgress>? progress)
    {
        var reason =
            $"refusing state at '{path}': {detail}. Nothing was replayed. Remove the file " +
            "as an administrator and reinstall, or investigate who wrote it";
        progress?.Report(new StepProgress(0, 0, reason, IsError: true));
        return new LoadAttempt(null, reason);
    }

    /// <summary>
    /// Best-effort delete of the scope-correct per-app state directory. Called on
    /// successful uninstall; failures are swallowed because the directory
    /// may legitimately still hold logs or other artefacts a future task
    /// chooses not to clean up.
    /// </summary>
    public static void Delete(string appId, InstallScope scope)
    {
        ArgumentException.ThrowIfNullOrEmpty(appId);
        var dir = DirectoryFor(appId, scope);
#pragma warning disable CA1031 // Best-effort cleanup; nothing the caller can do with the exception.
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // Best-effort.
        }
#pragma warning restore CA1031
    }
}
