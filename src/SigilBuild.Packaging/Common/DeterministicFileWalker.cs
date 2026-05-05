using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

namespace SigilBuild.Packaging.Common;

public readonly record struct WalkedFile(string AbsolutePath, string RelativePath, long Length);

public static class DeterministicFileWalker
{
    public static IEnumerable<WalkedFile> Walk(
        string sourceDirectory,
        IReadOnlyList<string>? include,
        IReadOnlyList<string>? exclude)
    {
        var matcher = new Matcher(System.StringComparison.OrdinalIgnoreCase);
        if (include is null || include.Count == 0) matcher.AddInclude("**/*");
        else foreach (var p in include) matcher.AddInclude(p);
        if (exclude is not null) foreach (var p in exclude) matcher.AddExclude(p);

        var sourceFull = Path.GetFullPath(sourceDirectory);
        var result = matcher.Execute(new DirectoryInfoWrapper(new DirectoryInfo(sourceFull)));

        return result.Files
            .Select(f => f.Path.Replace('\\', '/'))
            .OrderBy(rel => rel, System.StringComparer.Ordinal)
            .Select(rel =>
            {
                var abs = Path.Combine(sourceFull, rel);
                var len = new FileInfo(abs).Length;
                return new WalkedFile(abs, rel, len);
            })
            .ToList();
    }
}
