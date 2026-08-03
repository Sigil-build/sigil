namespace SigilBuild.Wrapper.Tests.Engine;

using System;
using System.Diagnostics;
using System.IO;
using FluentAssertions;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Tests.Helpers;

/// <summary>
/// Register row R16: containment is re-implemented three times, shared nowhere,
/// and no step destination is checked at all. These pin the single helper the
/// step catalog will route through — including the case that a textual
/// <c>StartsWith</c> cannot see: a directory junction planted inside the root.
/// </summary>
public sealed class PathContainmentTests
{
    // Windows-only: Path.GetFullPath does not treat '\' as a separator on Unix,
    // so these rows describe Windows path semantics rather than the helper.
    [WindowsTheory("Windows path semantics")]
    [InlineData(@"C:\Program Files\App", @"C:\Program Files\App\bin\a.exe", true)]
    [InlineData(@"C:\Program Files\App", @"C:\Program Files\App", true)]
    [InlineData(@"C:\Program Files\App", @"C:\Program Files\App\..\Other\a.exe", false)]
    [InlineData(@"C:\Program Files\App", @"C:\Windows\System32\a.dll", false)]
    [InlineData(@"C:\Program Files\App", @"C:\Program Files\AppEvil\a.exe", false)]
    [InlineData(@"C:\Program Files\App", @"\\server\share\a.exe", false)]
    public void IsUnder_contains_only_real_descendants(string root, string candidate, bool expected)
        => PathContainment.IsUnder(root, candidate).Should().Be(expected);

    [WindowsFact("Windows directory junctions")]
    public void IsUnderWithoutTraversal_rejects_a_directory_junction_in_the_chain()
    {
        using var root = new TempDir();
        using var outside = new TempDir();
        var link = Path.Combine(root.Path, "link");

        // Directory junctions require no privilege — this is the realistic
        // redirection primitive, not symlinks (Directory.CreateSymbolicLink
        // throws for an unelevated session with Developer Mode off).
        CreateJunctionOrFail(link, outside.Path);

        var target = Path.Combine(link, "config.json");

        PathContainment.IsUnder(root.Path, target).Should().BeTrue(
            "the textual path still looks contained");
        PathContainment.IsUnderWithoutTraversal(root.Path, target).Should().BeFalse(
            "following the junction escapes the root, which is the actual bug");
    }

    [WindowsFact("Windows directory junctions")]
    public void IsUnderWithoutTraversal_accepts_a_real_descendant()
    {
        using var root = new TempDir();
        var nested = Path.Combine(root.Path, "sub", "deeper");
        Directory.CreateDirectory(nested);

        // Both an existing directory and a not-yet-created leaf must pass: the
        // resolver checks a destination before anything is laid down.
        PathContainment.IsUnderWithoutTraversal(root.Path, nested).Should().BeTrue();
        PathContainment.IsUnderWithoutTraversal(root.Path, Path.Combine(nested, "not-created-yet.txt"))
            .Should().BeTrue();
    }

    /// <summary>
    /// Create a real NTFS directory junction (a reparse point) and assert it
    /// exists. A test that silently degrades to "no junction" would pass
    /// vacuously, which is precisely the defect this track exists to eliminate.
    /// </summary>
    private static void CreateJunctionOrFail(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("mklink");
        psi.ArgumentList.Add("/J");
        psi.ArgumentList.Add(link);
        psi.ArgumentList.Add(target);

        using var proc = Process.Start(psi);
        proc.Should().NotBeNull("cmd.exe must start so the junction can be created");
        var stdout = proc!.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        proc.ExitCode.Should().Be(0,
            $"'mklink /J' must succeed — junctions need no privilege. stdout: {stdout} stderr: {stderr}");
        Directory.Exists(link).Should().BeTrue("the junction directory entry must exist");
        File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint).Should().BeTrue(
            "the test is vacuous unless a real reparse point was created");
    }
}
