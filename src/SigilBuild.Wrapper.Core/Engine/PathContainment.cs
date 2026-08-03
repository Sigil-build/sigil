namespace SigilBuild.Wrapper.Engine;

using System;
using System.IO;

/// <summary>
/// The single path-containment predicate for the install engine (register row
/// R16). Answers "does <c>candidate</c> really live inside <c>root</c>?" — both
/// textually (after canonicalization) and physically (no reparse point on the
/// way down).
/// </summary>
/// <remarks>
/// <para>
/// A naive <c>candidate.StartsWith(root)</c> is wrong twice over:
/// <c>C:\Program Files\AppEvil</c> passes as a child of <c>C:\Program Files\App</c>,
/// and a <em>directory junction</em> planted inside the root redirects the write
/// anywhere the attacker likes. Junctions need no privilege on Windows, so that
/// second case is the realistic redirection primitive — not symlinks.
/// </para>
/// <para>
/// Both members fail closed: any unexpected exception yields <c>false</c>.
/// </para>
/// <para>
/// The pre-existing <c>payload://</c> traversal guard
/// (<see cref="StepContext.ResolvePath"/>) and the zip-slip guard in
/// <c>PayloadExtraction</c> deliberately keep their own copies of this logic for
/// now; they are verified sound and re-routing them during a security fix would
/// be gratuitous risk. Folding them into this helper is a post-v1 cleanup.
/// </para>
/// </remarks>
internal static class PathContainment
{
    /// <summary>
    /// True when <paramref name="candidate"/> resolves inside
    /// <paramref name="root"/> (or is <paramref name="root"/> itself).
    /// Canonicalizes both with <see cref="Path.GetFullPath(string)"/> BEFORE
    /// comparing, terminates the root with a directory separator so
    /// <c>C:\rootevil</c> cannot pass as <c>C:\root</c>, and compares
    /// <see cref="StringComparison.OrdinalIgnoreCase"/>. Returns <c>false</c> on
    /// any exception.
    /// </summary>
    public static bool IsUnder(string root, string candidate)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        try
        {
            var rootFull = Path.GetFullPath(root);
            var candidateFull = Path.GetFullPath(candidate);

            if (string.Equals(rootFull, candidateFull, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var rootPrefix = rootFull.EndsWith(Path.DirectorySeparatorChar)
                ? rootFull
                : rootFull + Path.DirectorySeparatorChar;

            return candidateFull.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase);
        }
#pragma warning disable CA1031 // Fail closed: a path this helper cannot canonicalize is not contained.
        catch (Exception)
        {
            return false;
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// <see cref="IsUnder"/>, plus: no component of the path from
    /// <paramref name="root"/> down to <paramref name="candidate"/> is a reparse
    /// point (junction or symlink). Directory junctions need no privilege on
    /// Windows, so this is the check that actually stops redirection out of the
    /// root. Components that do not exist yet cannot redirect anything and are
    /// accepted — a destination is validated before it is created.
    /// </summary>
    public static bool IsUnderWithoutTraversal(string root, string candidate)
    {
        if (!IsUnder(root, candidate))
        {
            return false;
        }

        try
        {
            var rootFull = Path.GetFullPath(root);
            var current = Path.GetFullPath(candidate);

            // Walk upward from the candidate to (but excluding) the root. The
            // root itself is the anchor the caller already trusts; every link in
            // the chain BELOW it is attacker-plantable and must be inspected.
            while (!string.Equals(current, rootFull, StringComparison.OrdinalIgnoreCase))
            {
                if (IsReparsePoint(current))
                {
                    return false;
                }

                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal))
                {
                    // Reached a volume root without meeting `rootFull`. IsUnder
                    // said otherwise, so treat the disagreement as untrusted.
                    return false;
                }

                current = parent;
            }

            return true;
        }
#pragma warning disable CA1031 // Fail closed: an unreadable component is not provably contained.
        catch (Exception)
        {
            return false;
        }
#pragma warning restore CA1031
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (IOException)
        {
            // Nothing exists at this component — it was not found, or the name is
            // one the filesystem cannot represent at all (the un-stamped runtime
            // resolves to a literal "<unset>" directory, which raises
            // ERROR_INVALID_NAME). Either way there is no reparse point here to
            // redirect a write, and the textual containment check has already
            // passed. FileNotFoundException / DirectoryNotFoundException derive
            // from IOException and are covered by this.
            //
            // UnauthorizedAccessException is deliberately NOT caught: a component
            // whose attributes we are not allowed to read is not provably free of
            // a reparse point, so it propagates and fails the check closed.
            return false;
        }
    }
}
