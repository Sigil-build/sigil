namespace SigilBuild.Wrapper.Engine;

using System;
using System.Globalization;

/// <summary>
/// Register row R16: every step destination is contained to <c>install_dir</c>,
/// and a brace token that never resolved fails the step instead of being written
/// to disk as a literal directory name.
/// </summary>
/// <remarks>
/// <para>
/// Before this, no step destination was checked at all.
/// <c>File.WriteAllText</c> traverses reparse points and truncates an existing
/// target in place, keeping its prior DACL — so an attacker-planted placeholder
/// stays attacker-writable after the elevated installer writes to it — and
/// <c>Directory.CreateDirectory</c> would happily materialize a whole tree
/// outside the install directory.
/// </para>
/// </remarks>
internal static class StepDestinationGuard
{
    /// <summary>
    /// The longest brace token this treats as a token rather than as literal
    /// filename text. Real tokens are short identifiers; the cap keeps a long
    /// run of braced text from being misread as one.
    /// </summary>
    private const int MaxTokenLength = 64;

    /// <summary>
    /// Returns <c>null</c> when <paramref name="resolved"/> is an acceptable
    /// destination for this run, or a step-failure message explaining the
    /// refusal.
    /// </summary>
    /// <param name="installDir">The run's resolved <c>install_dir</c> (<c>ctx.InstallDir</c>).</param>
    /// <param name="stepType">Manifest step type, e.g. <c>file_copy</c>.</param>
    /// <param name="field">Manifest field carrying the destination, e.g. <c>to</c>.</param>
    /// <param name="resolved">The destination AFTER the step's own token expansion.</param>
    /// <param name="allowOutsideInstallDir">
    /// The step's <c>allow_outside_install_dir</c> opt-out. Suppresses the
    /// containment check only — an unresolved token is always a failure, since it
    /// is a manifest typo under any policy.
    /// </param>
    public static string? Check(
        string? installDir, string stepType, string field, string resolved, bool allowOutsideInstallDir)
    {
        var token = FirstUnresolvedToken(resolved);
        if (token is not null)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{stepType}: the '{field}' path still contains the unresolved token '{{{token}}}' " +
                $"after substitution ('{resolved}'). Refusing to use it as a path — writing it " +
                $"verbatim would create a directory literally named '{{{token}}}'. Check the " +
                $"spelling: an installer.vars entry must be declared before '{{var.<name>}}' " +
                $"can expand.");
        }

        if (allowOutsideInstallDir)
        {
            return null;
        }

        // No anchor to check against. Production always has one:
        // StepContext.From calls InstallDirResolver.Resolve, which never returns
        // null, so this branch is reachable only from a hand-built context (the
        // step unit tests). StepContextInstallDirIsAlwaysResolvedTests pins that.
        if (string.IsNullOrWhiteSpace(installDir))
        {
            return null;
        }

        if (!PathContainment.IsUnderWithoutTraversal(installDir, resolved))
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{stepType}: the '{field}' path '{resolved}' is outside install_dir " +
                $"('{installDir}'), or reaches it through a directory junction. Set " +
                $"'allow_outside_install_dir: true' on this step if the write really is " +
                $"meant to land outside the installed application.");
        }

        return null;
    }

    /// <summary>
    /// The name of the first <c>{token}</c> still present in
    /// <paramref name="value"/>, or <c>null</c> when there is none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Purely lexical, and deliberately run over the ALREADY-RESOLVED string
    /// rather than over the raw manifest template. That has two consequences that
    /// matter:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///     <b>No allow-list to maintain, and no way to fall behind another lane.</b>
    ///     Anything <see cref="StepContext.Resolve"/> knows about has already been
    ///     substituted away by the time this runs, so a token added later
    ///     (<c>{staging_dir}</c> is the live example) is automatically accepted
    ///     with no change here. A hard-coded list would have had to be extended in
    ///     lockstep, and would have had to accept the whole <c>{var.*}</c> family
    ///     wholesale — which would let the very typo this exists to catch through.
    ///   </description></item>
    ///   <item><description>
    ///     <b>This never resolves anything itself.</b> It inspects a string the
    ///     caller already had. Resolving <c>{staging_dir}</c> has a side effect —
    ///     it creates the staging directory — and can throw when elevated, so a
    ///     validator that resolved in order to validate could create directories,
    ///     or fail an install, merely by checking a path.
    ///   </description></item>
    /// </list>
    /// <para>
    /// A token is <c>{</c>, an identifier, <c>}</c>: it must start with a letter
    /// or underscore and continue with letters, digits, <c>_</c> or <c>.</c>. That
    /// shape covers every token the engine defines and excludes the one realistic
    /// false positive, a braced GUID directory name
    /// (<c>{3f2504e0-4f89-11d3-9a0c-0305e82c3301}</c>), whose hyphens keep it out.
    /// </para>
    /// </remarks>
    internal static string? FirstUnresolvedToken(string? value)
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
