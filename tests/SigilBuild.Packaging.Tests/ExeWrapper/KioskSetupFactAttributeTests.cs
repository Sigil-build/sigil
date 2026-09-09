using System;
using System.IO;
using FluentAssertions;
using Xunit;

namespace SigilBuild.Packaging.Tests.ExeWrapper;

/// <summary>
/// Register row R6: <see cref="KioskSetupFactAttribute.SetupPath"/> previously walked six
/// fixed ".." segments up from the test assembly's output directory and landed OUTSIDE the
/// repository (e.g. resolving to a "tests\kiosk\..." path under the repo's PARENT directory),
/// so the guarded test could never run on any machine. This test pins the fix: the resolved
/// path must always be inside the repository root, found by walking up to <c>Sigil.slnx</c>
/// rather than by counting directory levels.
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
