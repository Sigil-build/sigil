namespace SigilBuild.Wrapper.Tests.Engine;

using System;
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
    // A trailing separator on either side must not change the answer. /D= keeps a
    // trailing '\' the operator typed, and S2.3 anchors on ctx.InstallDir.
    [InlineData(@"C:\Program Files\App\", @"C:\Program Files\App\bin\a.exe", true)]
    [InlineData(@"C:\Program Files\App\", @"C:\Program Files\App", true)]
    [InlineData(@"C:\Program Files\App", @"C:\Program Files\App\", true)]
    [InlineData(@"C:\Program Files\App\", @"C:\Program Files\App\", true)]
    [InlineData(@"C:\Program Files\App\", @"C:\Program Files\AppEvil\a.exe", false)]
    public void IsUnder_contains_only_real_descendants(string root, string candidate, bool expected)
        => PathContainment.IsUnder(root, candidate).Should().Be(expected);

    /// <summary>
    /// Win32 device-namespace spellings (<c>\\.\</c>, <c>\\?\</c>) reach the same
    /// file as the plain DOS path and pass an ACL read, so they are an alias a
    /// containment check has to have an answer for. Raised by lane S1, which hit
    /// the same aliasing in its trust predicate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The answer is <b>refuse</b>, and it falls out of canonicalisation rather
    /// than from a special case: <see cref="Path.GetFullPath(string)"/> collapses
    /// <c>..</c> but <b>preserves</b> the <c>\\.\</c> / <c>\\?\</c> prefix, so a
    /// device-spelled candidate can never share a prefix with a plainly-spelled
    /// root and <see cref="PathContainment.IsUnder"/> answers false. Fail-closed in
    /// both directions — a device-spelled root does not admit a plain candidate
    /// either.
    /// </para>
    /// <para>
    /// These rows exist because that is <em>load-bearing behaviour that looks like
    /// an oversight</em>. "Normalising" the prefix away in
    /// <c>PathContainment.Canonicalize</c> — a plausible tidy-up — would silently
    /// turn every one of these refusals into an admission, and for the
    /// privileged-step guard that means a SYSTEM-level target admitted under an
    /// alias. The price is that a device-path spelling of an otherwise legitimate
    /// location is rejected; no real installer writes one.
    /// </para>
    /// </remarks>
    [WindowsTheory("Windows path semantics")]
    // Device-spelled candidate against a plainly-spelled root.
    [InlineData(@"C:\Program Files\App", @"\\.\C:\Program Files\App\evil.exe")]
    [InlineData(@"C:\Program Files\App", @"\\?\C:\Program Files\App\evil.exe")]
    // …with a traversal folded in, which GetFullPath DOES collapse — proving the
    // refusal comes from the prefix, not from the '..'.
    [InlineData(@"C:\Program Files\App", @"\\.\C:\Program Files\App\..\..\Windows\System32\cmd.exe")]
    [InlineData(@"C:\Program Files\App", @"\\?\C:\Program Files\App\..\..\Windows\System32\cmd.exe")]
    // The reverse: a device-spelled root must not admit a plain candidate.
    [InlineData(@"\\.\C:\Program Files\App", @"C:\Program Files\App\evil.exe")]
    [InlineData(@"\\?\C:\Program Files\App", @"C:\Program Files\App\evil.exe")]
    public void IsUnder_refuses_a_device_namespace_alias(string root, string candidate)
    {
        PathContainment.IsUnder(root, candidate).Should().BeFalse(
            "a device-namespace spelling must not be admitted against a differently-spelled " +
            "root — see the remarks on this method");
        PathContainment.IsUnderWithoutTraversal(root, candidate).Should().BeFalse();
    }

    [WindowsFact("Windows directory junctions")]
    public void IsUnderWithoutTraversal_rejects_a_directory_junction_in_the_chain()
    {
        using var root = new TempDir();
        using var outside = new TempDir();
        var link = Path.Combine(root.Path, "link");

        // Directory junctions require no privilege — this is the realistic
        // redirection primitive, not symlinks (Directory.CreateSymbolicLink
        // throws for an unelevated session with Developer Mode off).
        Junction.CreateOrFail(link, outside.Path);

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

    // ── Trailing separators (fix round 1, Important 3) ────────────────────────

    [WindowsFact("Windows directory junctions")]
    public void IsUnderWithoutTraversal_is_unaffected_by_a_trailing_separator()
    {
        using var root = new TempDir();
        var nested = Path.Combine(root.Path, "sub", "deeper");
        Directory.CreateDirectory(nested);

        var rootWithSep = root.Path + Path.DirectorySeparatorChar;

        // Before the fix this returned False: GetFullPath preserves the trailing
        // separator while GetDirectoryName strips it, so the upward walk ran past
        // the anchor to the volume root and refused a reparse-free descendant.
        PathContainment.IsUnderWithoutTraversal(rootWithSep, nested).Should().BeTrue(
            "a trailing separator on the root is a formatting detail, not an escape");
        PathContainment.IsUnderWithoutTraversal(rootWithSep, nested + Path.DirectorySeparatorChar)
            .Should().BeTrue("nor is one on the candidate");
        PathContainment.IsUnderWithoutTraversal(rootWithSep, rootWithSep).Should().BeTrue(
            "the root is contained in itself however it is spelled");
    }

    [WindowsFact("Windows directory junctions")]
    public void IsUnderWithoutTraversal_still_rejects_a_junction_under_a_trailing_separator_root()
    {
        using var root = new TempDir();
        using var outside = new TempDir();
        var link = Path.Combine(root.Path, "link");
        Junction.CreateOrFail(link, outside.Path);

        // The trailing-separator fix must not have loosened the actual guard.
        PathContainment.IsUnderWithoutTraversal(
            root.Path + Path.DirectorySeparatorChar, Path.Combine(link, "config.json"))
            .Should().BeFalse();
    }

    // ── IsReparsePoint exception paths (fix round 1, Important 1) ─────────────

    [WindowsFact("Windows reparse points")]
    public void IsReparsePoint_is_true_only_for_an_actual_reparse_point()
    {
        using var root = new TempDir();
        var plainDir = Path.Combine(root.Path, "plain");
        Directory.CreateDirectory(plainDir);
        var plainFile = Path.Combine(root.Path, "plain.txt");
        File.WriteAllText(plainFile, "x");

        using var outside = new TempDir();
        var link = Path.Combine(root.Path, "link");
        Junction.CreateOrFail(link, outside.Path);

        PathContainment.IsReparsePoint(link).Should().BeTrue("a junction is a reparse point");
        PathContainment.IsReparsePoint(plainDir).Should().BeFalse();
        PathContainment.IsReparsePoint(plainFile).Should().BeFalse();
    }

    [WindowsFact("Windows reparse points")]
    public void IsReparsePoint_swallows_only_the_three_nothing_is_here_conditions()
    {
        using var root = new TempDir();

        // FileNotFoundException (0x80070002) — no such leaf.
        PathContainment.IsReparsePoint(Path.Combine(root.Path, "no-such-file"))
            .Should().BeFalse();

        // DirectoryNotFoundException (0x80070003) — no such parent…
        PathContainment.IsReparsePoint(Path.Combine(root.Path, "no-such-dir", "leaf.txt"))
            .Should().BeFalse();

        // …and the missing-volume case, which reports the same way.
        PathContainment.IsReparsePoint(@"Z:\nope\file.txt").Should().BeFalse();

        // IOException / ERROR_INVALID_NAME (0x8007007B) — a name the filesystem
        // cannot represent. This is the un-stamped runtime's literal "<unset>"
        // AppId directory, the case that made the catch necessary at all.
        PathContainment.IsReparsePoint(Path.Combine(root.Path, "<unset>")).Should().BeFalse();

        // …and an over-length single component, which reports the same way.
        PathContainment.IsReparsePoint(Path.Combine(root.Path, new string('a', 400)))
            .Should().BeFalse();
    }

    [WindowsFact("Windows reparse points")]
    public void IsReparsePoint_propagates_anything_else_so_callers_fail_closed()
    {
        using var root = new TempDir();

        // PathTooLongException (0x800700CE) derives from IOException and WAS
        // swallowed by the previous blanket catch. It must now propagate.
        var overlong = () => PathContainment.IsReparsePoint(
            Path.Combine(root.Path, new string('a', 40000)));
        overlong.Should().Throw<PathTooLongException>();

        // A non-IOException is not caught either.
        var embeddedNul = () => PathContainment.IsReparsePoint("bad\0path");
        embeddedNul.Should().Throw<ArgumentException>();
    }

    [WindowsFact("Windows reparse points")]
    public void IsUnderWithoutTraversal_fails_closed_when_a_component_cannot_be_read()
    {
        using var root = new TempDir();

        // The propagating cases above must surface as "not contained" — never as
        // an exception out of the containment helper, and never as True.
        PathContainment.IsUnderWithoutTraversal(root.Path, Path.Combine(root.Path, new string('a', 40000)))
            .Should().BeFalse("an uninterrogable component is not provably contained");
    }

}
