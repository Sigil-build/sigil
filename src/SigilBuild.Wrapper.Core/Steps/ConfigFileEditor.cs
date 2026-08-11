namespace SigilBuild.Wrapper.Steps;

using System;
using System.IO;
using SigilBuild.Wrapper.Engine;

/// <summary>
/// Shared scaffold for the P8 config-file edit steps (<c>ini_write</c> /
/// <c>json_edit</c> / <c>xml_edit</c>). It journals the ENTIRE prior file (or its
/// absence) for byte-exact rollback, then applies a format-specific transform and
/// writes the result. A missing file with <c>create_if_missing=false</c> fails.
/// </summary>
internal static class ConfigFileEditor
{
    /// <param name="transform">
    /// Produces the new file content from the current content — <c>null</c> when the
    /// file does not yet exist (a <c>create_if_missing</c> edit). Throwing surfaces
    /// as a step failure (the file is left untouched; the journal entry recorded
    /// before the write makes rollback a no-op).
    /// </param>
    /// <param name="stepType">
    /// The manifest step type (<c>ini_write</c> / <c>json_edit</c> /
    /// <c>xml_edit</c>), used to name the step in an R16 containment refusal.
    /// </param>
    /// <param name="allowOutsideInstallDir">
    /// The step's <c>allow_outside_install_dir</c> opt-out (R16).
    /// </param>
    public static StepResult Edit(
        StepContext ctx, RollbackJournal journal, string rawPath, bool createIfMissing,
        Func<string?, string> transform, string stepType, bool allowOutsideInstallDir)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(journal);

        var path = ctx.ResolvePath(rawPath);

        // R16: File.WriteAllText below traverses reparse points and truncates an
        // existing target in place, keeping its DACL — so an attacker-planted
        // placeholder outside install_dir stays attacker-writable after the
        // elevated installer writes to it — and Directory.CreateDirectory would
        // materialize the whole tree. Refused before the file is even stat'ed.
        var refusal = StepDestinationGuard.Check(
            ctx.InstallDir, stepType, "path", path, allowOutsideInstallDir);
        if (refusal is not null)
        {
            return new StepResult(false, refusal);
        }

        var existed = File.Exists(path);
        if (!existed && !createIfMissing)
        {
            return new StepResult(false, $"file '{path}' does not exist and create_if_missing is false");
        }

        // Snapshot the ENTIRE prior file (or record its absence) BEFORE writing, so
        // a crash or a later step failure restores the exact original bytes.
        string? stash = null;
        if (existed)
        {
            stash = Path.Combine(Path.GetTempPath(), "sigil-cfg-" + Guid.NewGuid().ToString("N"));
            File.Copy(path, stash, overwrite: false);
        }
        journal.Append(new RollbackRecord.RestoreConfigFile(path, stash));

        var current = existed ? File.ReadAllText(path) : null;

        string updated;
#pragma warning disable CA1031 // A malformed file / bad pointer / bad xpath is a typed step failure, not a crash.
        try
        {
            updated = transform(current);
        }
        catch (Exception ex)
        {
            return new StepResult(false, ex.Message);
        }
#pragma warning restore CA1031

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllText(path, updated);
        return new StepResult(true, null);
    }
}
