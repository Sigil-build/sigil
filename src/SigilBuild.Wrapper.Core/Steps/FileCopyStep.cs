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
        var from = ctx.ResolvePath(_spec.From);
        var to = ctx.Resolve(_spec.To);
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
