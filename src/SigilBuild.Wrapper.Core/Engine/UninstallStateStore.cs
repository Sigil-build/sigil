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
    /// <summary>The two candidate scopes, searched in preference order by <see cref="TryLoad"/>.</summary>
    private static readonly InstallScope[] AllScopes = { InstallScope.Machine, InstallScope.User };

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
    public sealed record LoadedState(RollbackJournal Journal, InstallScope Scope);

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
    /// </summary>
    public static void Save(
        string appId,
        RollbackJournal journal,
        InstallScope scope,
        System.Collections.Generic.IReadOnlyList<string>? secretValues = null,
        IProgress<StepProgress>? progress = null)
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
    /// honoring the scope it was installed under (T12). The
    /// <paramref name="preferredScope"/> (resolved from the uninstall command
    /// line, e.g. the <c>/allusers</c> ARP <c>UninstallString</c>) is searched
    /// first; if absent there, the opposite scope's directory is tried so an
    /// interactive uninstall still locates state. The returned
    /// <see cref="LoadedState.Scope"/> is the scope recorded <em>in</em> the file
    /// and drives ARP-hive / state-dir selection. Returns <c>null</c> when no
    /// state file exists in either scope or the JSON is unreadable; the caller
    /// (<c>UninstallEngine</c>) translates that into the documented "no uninstall
    /// state found" error. Machine-scope state is refused outright (R1) unless BOTH
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
    /// and was rejected on R1 provenance grounds. Reporting that as "no uninstall
    /// state found" would tell the operator the opposite of what happened and would
    /// mask an attack, so <c>UninstallEngine</c> consumes this shape.
    /// </summary>
    public static LoadAttempt Load(
        string appId,
        InstallScope preferredScope,
        IProgress<StepProgress>? progress = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(appId);

        // Search the preferred scope first, then the other, so uninstall finds
        // state regardless of a missing/auto scope flag.
        var order = preferredScope == InstallScope.Machine
            ? AllScopes
            : new[] { InstallScope.User, InstallScope.Machine };

        foreach (var dirScope in order)
        {
            var path = PathFor(appId, dirScope);
            if (!File.Exists(path))
            {
                continue;
            }

            // R1: an unprivileged user can pre-create %ProgramData%\Sigil\<AppId>
            // and become CREATOR OWNER of the file the elevated uninstall later
            // replays. Refuse rather than replay, and say so — and do NOT fall
            // through to the other scope, because a silent skip here reads as
            // "no prior install" and would mask an attack.
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

            var json = File.ReadAllText(path);
            SerializableRollbackJournal? s;
            try
            {
                s = JsonSerializer.Deserialize(
                    json,
                    WrapperBlobJsonContext.Default.SerializableRollbackJournal);
            }
#pragma warning disable CA1031 // Corrupt state file → skip → caller surfaces a clear error.
            catch
            {
                continue;
            }
#pragma warning restore CA1031
            if (s is null)
            {
                continue;
            }

            var journal = new RollbackJournal();
            foreach (var rec in s.Records)
            {
                journal.Append(rec.ToRollbackRecord());
            }
            // Honor the scope recorded in the file (falls back to the directory's
            // scope for pre-T12 state that predates the recorded field).
            return new LoadAttempt(new LoadedState(journal, s.Scope), null);
        }

        return new LoadAttempt(null, null);
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
