using System;
using System.IO;
using FluentAssertions;
using Xunit;

namespace SigilBuild.Packaging.Tests.ExeWrapper;

/// <summary>
/// Guards: <see cref="KioskSetupFactAttribute.SetupPath"/> must resolve to a path inside
/// the repository root, found by walking up to <c>Sigil.slnx</c> rather than by counting
/// a fixed number of ".." segments — a fixed count can land OUTSIDE the repository (e.g.
/// resolving to a "tests\kiosk\..." path under the repo's PARENT directory), leaving the
/// guarded test unable to ever run on any machine. (R6)
/// </summary>
public class KioskSetupFactAttributeTests
{
    [Fact]
    public void SetupPath_IsInsideRepositoryRoot()
    {
        var repoRoot = FindRepoRoot(AppContext.BaseDirectory);

        KioskSetupFactAttribute.SetupPath.Should().StartWith(
            repoRoot + Path.DirectorySeparatorChar,
            "the kiosk sample path must resolve inside the repository, not walk past its root");
    }

    [Fact]
    public void SetupPath_EndsWithTheKnownKioskSampleRelativePath()
    {
        var expectedSuffix = Path.Combine(
            "tests", "kiosk", "dist", "Embed.Infinity.Kiosk-1.0.0-x64-Setup.exe");

        KioskSetupFactAttribute.SetupPath.Should().EndWith(expectedSuffix);
    }

    private static string FindRepoRoot(string startDirectory)
    {
        for (var dir = new DirectoryInfo(startDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Sigil.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException(
            $"could not locate the Sigil repo root (no Sigil.slnx found) walking up from '{startDirectory}'");
    }
}
