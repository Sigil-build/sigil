// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;

namespace SigilBuild.Wrapper.Steps;

internal sealed class FileCopyStep : IStep
{
    private readonly InstallStep.FileCopy _spec;

    public FileCopyStep(InstallStep.FileCopy spec)
    {
        _spec = spec;
    }

    public Task<StepResult> RunAsync(StepContext ctx, RollbackJournal journal, CancellationToken ct)
    {
        // Source may be a payload:// URI (rebased onto the extracted payload
        // root); destination is always a real install-side path. `to` is a path
        // field, so it resolves through ResolvePath — which applies the payload://
        // traversal guard every path-taking step gets — not plain Resolve (R16).
        var from = ctx.ResolvePath(_spec.From);
        var to = ctx.ResolvePath(_spec.To);

        // Contain the destination BEFORE Directory.CreateDirectory, which would
        // otherwise materialize a whole tree outside install_dir — and, where the
        // manifest carried an unresolved token, a tree whose top directory is
        // literally named "{var.typo}" (R16).
        var refusal = StepDestinationGuard.Check(
            ctx.InstallDir, "file_copy", "to", to, _spec.AllowOutsideInstallDir);
        if (refusal is not null)
        {
            return Task.FromResult(StepResult.Failed(refusal));
        }

        CreateDirectoriesJournaled(to, journal);

        var (rootDir, pattern, recurse) = SplitGlob(from);
        if (!Directory.Exists(rootDir))
        {
            return Task.FromResult(StepResult.Failed($"glob root '{rootDir}' does not exist"));
        }

        var searchOption = recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        foreach (var src in Directory.EnumerateFiles(rootDir, pattern, searchOption))
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(rootDir, src);
            var dst = Path.Combine(to, rel);
            CreateDirectoriesJournaled(Path.GetDirectoryName(dst)!, journal);

            var existed = File.Exists(dst);

            // R77: overwrite:false means an existing file at the destination is
            // already the state the manifest asked for — the flag exists to protect a
            // user's settings across an upgrade — so leave it and move on. No backup
            // and no journal record either: nothing is written, so there is nothing to
            // undo, and journalling a restore of bytes we never touched would cost a
            // full copy and strand a .sigil-bak file for no gain.
            //
            // The condition used to be `overwrite: _spec.Overwrite || existed`, which
            // is unconditionally true in exactly the case the flag was written for, so
            // the flag never once prevented an overwrite.
            if (existed && !_spec.Overwrite)
            {
                ctx.ProgressSink?.Report(new StepProgress(
                    0, 0, $"file_copy: kept existing {Path.GetFileName(dst)}", false));
                continue;
            }

            string? backup = null;
            if (existed)
            {
                backup = dst + ".sigil-bak";
                File.Copy(dst, backup, overwrite: true);
            }

            // Record rollback BEFORE the write so a crash leaves the journal correct.
            journal.Append(new RollbackRecord.RestoreFile(dst, existed, backup));

            // Past the guard above, `existed` implies Overwrite, so this is always a
            // sanctioned replacement.
            File.Copy(src, dst, overwrite: true);
        }
        return Task.FromResult(StepResult.Ok());
    }

    /// <summary>
    /// Create <paramref name="dir"/> and every missing level above it, recording a
    /// <see cref="RollbackRecord.RemoveDirectory"/> for each level this call actually
    /// created — and for none that were already there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>file_copy</c> used to call <c>Directory.CreateDirectory</c> directly and
    /// journal only the files. Directories were therefore created and never recorded,
    /// so neither rollback nor uninstall removed them: uninstalling left the whole
    /// directory skeleton behind, empty, permanently. The journal is the record of
    /// what this run changed, and a directory it created is something it changed.
    /// </para>
    /// <para>
    /// <c>Directory.CreateDirectory</c> silently creates every missing parent and does
    /// not report which ones it made, so the levels are probed first. Anything that
    /// already existed is deliberately NOT recorded — removing a directory the user
    /// had is the one mistake worse than leaving one behind.
    /// </para>
    /// <para>
    /// Records are appended shallowest-first because <c>UndoAsync</c> replays in
    /// reverse, so the deepest directory is removed first and each parent is empty by
    /// the time its own record runs. Each removal is itself conditional on the
    /// directory being empty, so a file the user put there afterwards keeps its
    /// directory alive.
    /// </para>
    /// </remarks>
    private static void CreateDirectoriesJournaled(string dir, RollbackJournal journal)
    {
        if (string.IsNullOrEmpty(dir) || Directory.Exists(dir))
        {
            return;
        }

        // Walk up collecting what is missing: deepest first, because that is the
        // direction the walk goes.
        var missing = new List<string>();
        for (var level = dir;
             !string.IsNullOrEmpty(level) && !Directory.Exists(level);
             level = Path.GetDirectoryName(level))
        {
            missing.Add(level);
        }

        Directory.CreateDirectory(dir);

        // Reverse to shallowest-first for the journal.
        for (var i = missing.Count - 1; i >= 0; i--)
        {
            journal.Append(new RollbackRecord.RemoveDirectory(missing[i]));
        }
    }

    /// <summary>
    /// Decompose a glob path like <c>C:/payload/base/**</c> into
    /// (rootDir=<c>C:/payload/base</c>, pattern=<c>*</c>, recurse=true).
    /// Plain filenames return (parent, name, false). Names with <c>**</c>
    /// recurse; with <c>*.txt</c> match only that pattern non-recursively.
    /// </summary>
    private static (string RootDir, string Pattern, bool Recurse) SplitGlob(string path)
    {
        var normalized = path.Replace('\\', '/');

        // Bare "**" → recurse-everything from the current working directory.
        // Without this branch, "**" would fall through to the no-slash case
        // below and become a literal filename pattern (matches zero real
        // files, since "**" can't appear in a Windows filename).
        if (normalized == "**")
        {
            return (Directory.GetCurrentDirectory(), "*", true);
        }

        if (normalized.EndsWith("/**", System.StringComparison.Ordinal))
        {
            return (normalized[..^3], "*", true);
        }

        if (normalized.Contains("/**/", System.StringComparison.Ordinal))
        {
            var idx = normalized.IndexOf("/**/", System.StringComparison.Ordinal);
            return (normalized[..idx], normalized[(idx + 4)..], true);
        }

        var lastSlash = normalized.LastIndexOf('/');
        if (lastSlash < 0)
        {
            return (Directory.GetCurrentDirectory(), normalized, false);
        }

        var name = normalized[(lastSlash + 1)..];
        var dir = normalized[..lastSlash];
        return (dir, name, false);
    }
}
