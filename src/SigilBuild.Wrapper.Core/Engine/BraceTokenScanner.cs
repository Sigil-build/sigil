namespace SigilBuild.Wrapper.Engine;

using System;

/// <summary>
/// Finds a <c>{token}</c> that survived substitution (register row R16).
/// </summary>
/// <remarks>
/// <para>
/// Consumed by <see cref="StepContext.ResolvePath"/>, so the check applies to
/// EVERY path-valued step field by construction rather than to whichever steps a
/// containment guard happens to have been wired into. Containment legitimately
/// varies per step — some writes deliberately land outside <c>install_dir</c> —
/// but "this path still contains an unresolved token" never does: it is a
/// manifest typo under any policy, in a <c>directory_create</c> exactly as much
/// as in a <c>file_copy</c>.
/// </para>
/// <para>
/// Purely lexical, and deliberately run over the ALREADY-RESOLVED string rather
/// than over the raw manifest template. Two consequences that matter:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>No allow-list to maintain, and no way to fall behind another lane.</b>
///     Anything <see cref="StepContext.Resolve"/> knows about has been
///     substituted away by the time this runs, so a token added later
///     (<c>{staging_dir}</c> is the live example) is automatically accepted with
///     no change here. A hard-coded list would have had to be extended in
///     lockstep, and would have had to accept the whole <c>{var.*}</c> family
///     wholesale — which would let through the very typo this exists to catch.
///   </description></item>
///   <item><description>
///     <b>Nothing is resolved in order to validate.</b> This inspects a string
///     the caller already had. Resolving <c>{staging_dir}</c> has a side effect —
///     it creates the staging directory — and can throw when elevated, so a
///     validator that resolved would be able to create directories, or fail an
///     install, merely by checking a path.
///   </description></item>
/// </list>
/// </remarks>
internal static class BraceTokenScanner
{
    /// <summary>
    /// The longest brace token treated as a token rather than as literal
    /// filename text. Real tokens are short identifiers; the cap keeps a long run
    /// of braced text from being misread as one.
    /// </summary>
    private const int MaxTokenLength = 64;

    /// <summary>
    /// The name of the first <c>{token}</c> still present in
    /// <paramref name="value"/>, or <c>null</c> when there is none.
    /// </summary>
    /// <remarks>
    /// A token is <c>{</c>, an identifier, <c>}</c>: it must start with an ASCII
    /// letter or underscore and continue with letters, digits, <c>_</c> or
    /// <c>.</c>. That shape covers every token the engine defines and excludes the
    /// one realistic false positive, a braced GUID directory name
    /// (<c>{3f2504e0-4f89-11d3-9a0c-0305e82c3301}</c>), whose hyphens keep it out.
    /// </remarks>
    public static string? FirstUnresolved(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var open = value.IndexOf('{', StringComparison.Ordinal);
        while (open >= 0 && open + 1 < value.Length)
        {
            var close = value.IndexOf('}', open + 1);
            if (close < 0)
            {
                return null;
            }

            var name = value[(open + 1)..close];
            if (IsTokenName(name))
            {
                return name;
            }

            open = value.IndexOf('{', open + 1);
        }

        return null;
    }

    private static bool IsTokenName(string name)
    {
        if (name.Length == 0 || name.Length > MaxTokenLength)
        {
            return false;
        }

        if (!char.IsAsciiLetter(name[0]) && name[0] != '_')
        {
            return false;
        }

        foreach (var c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_' && c != '.')
            {
                return false;
            }
        }

        return true;
    }
}
