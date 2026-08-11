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
        // root); destination is always a real install-side path.
        //
        // R16: `to` went through ctx.Resolve, not ctx.ResolvePath — so it bypassed
        // even the payload:// traversal guard every other path-taking step gets.
        // It is a path field and now resolves like one.
        var from = ctx.ResolvePath(_spec.From);
        var to = ctx.ResolvePath(_spec.To);

        // R16: contain the destination BEFORE Directory.CreateDirectory, which
        // would otherwise materialize a whole tree outside install_dir — and,
        // where the manifest carried an unresolved token, a tree whose top
        // directory is literally named "{var.typo}".
        var refusal = StepDestinationGuard.Check(
            ctx.InstallDir, "file_copy", "to", to, _spec.AllowOutsideInstallDir);
        if (refusal is not null)
        {
            return Task.FromResult(StepResult.Failed(refusal));
        }

        Directory.CreateDirectory(to);

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
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);

            var existed = File.Exists(dst);
            string? backup = null;
            if (existed)
            {
                backup = dst + ".sigil-bak";
                File.Copy(dst, backup, overwrite: true);
            }

            // Record rollback BEFORE the write so a crash leaves the journal correct.
            journal.Append(new RollbackRecord.RestoreFile(dst, existed, backup));

            File.Copy(src, dst, overwrite: _spec.Overwrite || existed);
        }
        return Task.FromResult(StepResult.Ok());
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
