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
    /// <c>ERROR_INVALID_NAME</c> (Win32 123) as an <see cref="IOException"/>
    /// HRESULT. Measured on .NET 10 / Windows: raised by
    /// <see cref="File.GetAttributes(string)"/> for a name the filesystem cannot
    /// represent at all.
    /// </summary>
    private const int HResultInvalidName = unchecked((int)0x8007007B);

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
            var rootFull = Canonicalize(root);
            var candidateFull = Canonicalize(candidate);

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
            var rootFull = Canonicalize(root);
            var current = Canonicalize(candidate);

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

    /// <summary>
    /// <see cref="Path.GetFullPath(string)"/> with any trailing directory
    /// separator removed (except on a volume/UNC root, where the separator is
    /// part of the path).
    /// </summary>
    /// <remarks>
    /// The trim matters for <see cref="IsUnderWithoutTraversal"/>, not for the
    /// prefix compare. <c>GetFullPath</c> PRESERVES a trailing separator while
    /// <see cref="Path.GetDirectoryName(string)"/> STRIPS it, so an untrimmed
    /// root of <c>C:\App\</c> could never equal any value the upward walk
    /// produces — the walk would run past the anchor to the volume root and
    /// refuse a genuine, reparse-free descendant. Task S2.3 anchors on
    /// <c>ctx.InstallDir</c>, which keeps the trailing <c>\</c> a user typed
    /// after <c>/D=</c>, so the shape is reachable there even though S2.2 never
    /// produces it.
    /// </remarks>
    private static string Canonicalize(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>
    /// True when <paramref name="path"/> is a reparse point (junction or
    /// symlink). A component that cannot exist is not one; anything we are
    /// unable to interrogate throws, and the callers above fail closed on that.
    /// </summary>
    /// <remarks>
    /// Exactly three conditions are treated as "nothing here to redirect a
    /// write", each verified against .NET 10 on Windows:
    /// <list type="bullet">
    ///   <item><description><see cref="FileNotFoundException"/> (0x80070002) — no such file.</description></item>
    ///   <item><description><see cref="DirectoryNotFoundException"/> (0x80070003) — no such parent, and the missing-volume case.</description></item>
    ///   <item><description><see cref="IOException"/> with <c>ERROR_INVALID_NAME</c> (0x8007007B) — a name the filesystem cannot represent, e.g. the un-stamped runtime's literal <c>&lt;unset&gt;</c> directory or an over-length component.</description></item>
    /// </list>
    /// Everything else propagates and fails the containment check closed —
    /// notably <see cref="UnauthorizedAccessException"/> (attributes we may not
    /// read are not provably free of a reparse point), <see cref="PathTooLongException"/>
    /// (0x800700CE), and lock / device-not-ready I/O errors. The previous
    /// blanket <c>catch (IOException)</c> swallowed those last two as well.
    /// </remarks>
    internal static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (IOException ex) when (ex.HResult == HResultInvalidName)
        {
            return false;
        }
    }
}
