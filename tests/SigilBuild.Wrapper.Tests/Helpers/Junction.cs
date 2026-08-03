namespace SigilBuild.Wrapper.Tests.Helpers;

using System.Diagnostics;
using System.IO;
using FluentAssertions;

/// <summary>
/// Creates real NTFS directory junctions for the R16 containment tests.
/// </summary>
/// <remarks>
/// <c>Directory.CreateSymbolicLink</c> is deliberately NOT used: it throws for an
/// unelevated session with Developer Mode off, so a symlink-based test would fail
/// on a developer box and on any non-elevated CI runner. A directory junction
/// needs no privilege at all, which is precisely why it is the realistic
/// redirection primitive an attacker reaches for.
/// </remarks>
internal static class Junction
{
    /// <summary>
    /// Create a junction at <paramref name="link"/> pointing at
    /// <paramref name="target"/>, asserting that a real reparse point resulted.
    /// A test that silently degraded to "no junction" would pass vacuously —
    /// exactly the defect this track exists to eliminate — so every failure mode
    /// is turned into a loud assertion failure.
    /// </summary>
    public static void CreateOrFail(string link, string target)
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

    /// <summary>
    /// Remove a junction without touching its target. Best-effort.
    /// </summary>
    public static void Remove(string link)
    {
        try { Directory.Delete(link); }
#pragma warning disable CA1031 // Best-effort cleanup of a test artifact.
        catch { /* best-effort */ }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Best-effort sweep of stale junctions left in <paramref name="directory"/>
    /// by an earlier run that was hard-killed between creation and its
    /// <c>finally</c>. Only entries whose name starts with
    /// <paramref name="prefix"/> AND which are actually reparse points are
    /// removed, so a real directory sharing the prefix is never deleted.
    /// </summary>
    /// <remarks>
    /// Junctions are the one test artifact where a leak is genuinely confusing
    /// later — a stray link under <c>%LocalAppData%\Programs</c> looks like an
    /// installed app. Call this at the start of any test that plants one.
    /// </remarks>
    public static void SweepStale(string directory, string prefix)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            foreach (var entry in Directory.GetDirectories(directory, prefix + "*"))
            {
                if (File.GetAttributes(entry).HasFlag(FileAttributes.ReparsePoint))
                {
                    Remove(entry);
                }
            }
        }
#pragma warning disable CA1031 // Best-effort sweep; never fail a test because of it.
        catch { /* best-effort */ }
#pragma warning restore CA1031
    }
}
