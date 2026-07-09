namespace SigilBuild.Wrapper.Engine;

using System;
using System.IO;
using System.Text.Json;
using SigilBuild.Wrapper.Json;

/// <summary>
/// Reads and writes the install-state JSON at
/// <c>%ProgramData%\Sigil\&lt;AppId&gt;\uninstall.json</c>. Used by Task 19's
/// auto-derived uninstall: after a successful install the engine snapshots
/// its <see cref="RollbackJournal"/> here, then on a later <c>/Uninstall</c>
/// invocation <c>UninstallEngine</c> rehydrates and replays it in reverse.
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
    /// <summary>Per-app state directory: <c>%ProgramData%\Sigil\&lt;AppId&gt;</c>.</summary>
    public static string DirectoryFor(string appId)
    {
        ArgumentException.ThrowIfNullOrEmpty(appId);
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Sigil",
            appId);
    }

    /// <summary>Full path to <c>uninstall.json</c> for the given app id.</summary>
    public static string PathFor(string appId) =>
        Path.Combine(DirectoryFor(appId), "uninstall.json");

    /// <summary>
    /// Persist <paramref name="journal"/> as <c>uninstall.json</c> under the
    /// per-app state directory, creating the directory if needed.
    /// </summary>
    public static void Save(string appId, RollbackJournal journal)
    {
        ArgumentException.ThrowIfNullOrEmpty(appId);
        ArgumentNullException.ThrowIfNull(journal);

        var dir = DirectoryFor(appId);
        Directory.CreateDirectory(dir);

        var records = new SerializableRollbackRecord[journal.Records.Count];
        for (var i = 0; i < journal.Records.Count; i++)
        {
            records[i] = journal.Records[i].ToSerializable();
        }

        var serializable = new SerializableRollbackJournal
        {
            AppId = appId,
            Version = "1",
            Records = records,
        };

        var json = JsonSerializer.Serialize(
            serializable,
            WrapperBlobJsonContext.Default.SerializableRollbackJournal);
        File.WriteAllText(PathFor(appId), json);
    }

    /// <summary>
    /// Load and rehydrate the persisted journal for <paramref name="appId"/>,
    /// returning <c>null</c> when no state file exists or the JSON is missing
    /// / unreadable. The caller (<c>UninstallEngine</c>) translates a null
    /// return into the documented "no uninstall state found" error.
    /// </summary>
    public static RollbackJournal? TryLoad(string appId)
    {
        ArgumentException.ThrowIfNullOrEmpty(appId);

        var path = PathFor(appId);
        if (!File.Exists(path))
        {
            return null;
        }

        var json = File.ReadAllText(path);
        SerializableRollbackJournal? s;
        try
        {
            s = JsonSerializer.Deserialize(
                json,
                WrapperBlobJsonContext.Default.SerializableRollbackJournal);
        }
#pragma warning disable CA1031 // Corrupt state file → null return → caller surfaces a clear error.
        catch
        {
            return null;
        }
#pragma warning restore CA1031
        if (s is null)
        {
            return null;
        }

        var journal = new RollbackJournal();
        foreach (var rec in s.Records)
        {
            journal.Append(rec.ToRollbackRecord());
        }
        return journal;
    }

    /// <summary>
    /// Best-effort delete of the per-app state directory. Called on
    /// successful uninstall; failures are swallowed because the directory
    /// may legitimately still hold logs or other artefacts a future task
    /// chooses not to clean up.
    /// </summary>
    public static void Delete(string appId)
    {
        ArgumentException.ThrowIfNullOrEmpty(appId);
        var dir = DirectoryFor(appId);
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
