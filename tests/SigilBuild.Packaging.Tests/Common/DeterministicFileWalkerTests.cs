using System.IO;
using System.Linq;
using FluentAssertions;
using SigilBuild.Packaging.Common;
using Xunit;

namespace SigilBuild.Packaging.Tests.Common;

public class DeterministicFileWalkerTests
{
    private static readonly string Source = Path.Combine("Fixtures", "sample-source");

    [Fact]
    public void Walk_NoPatterns_ReturnsAllFilesSortedByRelativePath()
    {
        var files = DeterministicFileWalker.Walk(Source, include: null, exclude: null).ToArray();

        files.Select(f => f.RelativePath.Replace('\\', '/')).Should().Equal(
            "app.exe", "assets/logo.png", "debug/app.pdb", "readme.txt");
    }

    [Fact]
    public void Walk_WithPdbExclude_OmitsPdbFiles()
    {
        var files = DeterministicFileWalker.Walk(Source,
            include: null,
            exclude: new[] { "**/*.pdb" }).ToArray();

        files.Select(f => f.RelativePath.Replace('\\', '/')).Should().NotContain("debug/app.pdb");
        files.Should().HaveCount(3);
    }

    [Fact]
    public void Walk_WithRestrictiveInclude_OnlyMatchesIncluded()
    {
        var files = DeterministicFileWalker.Walk(Source,
            include: new[] { "*.exe", "*.txt" },
            exclude: null).ToArray();

        files.Select(f => f.RelativePath.Replace('\\', '/')).Should().Equal("app.exe", "readme.txt");
    }
}
