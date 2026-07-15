namespace SigilBuild.Wrapper.Engine;

using System;

/// <summary>
/// The single version-comparison implementation, shared by the
/// <c>version_gte(...)</c> expression function and the P3 version-aware upgrade
/// decision (<see cref="UpgradePlanner"/>). Uses <see cref="System.Version"/>'s
/// numeric dotted-quad ordering, falling back to an ordinal string compare when a
/// value is not a parseable dotted version.
/// </summary>
/// <remarks>
/// <para>
/// PRE-RELEASE / SemVer HANDLING: <see cref="System.Version"/> only accepts the
/// numeric dotted forms with at least two components (<c>1.2</c>, <c>1.2.3</c>,
/// <c>1.2.3.4</c> — a bare <c>1</c> does NOT parse). A
/// SemVer pre-release / build tag (<c>1.2.0-rc1</c>, <c>1.2.0+build</c>) is NOT
/// parseable and therefore NOT interpreted as "older than 1.2.0" — it falls back
/// to a plain lexicographic (ordinal) compare. Sigil deliberately does not model
/// SemVer pre-release precedence.
/// </para>
/// <para>
/// For the upgrade decision (<see cref="UpgradePlanner"/>) this is intentionally
/// conservative: an unparseable <em>installed</em> version is classified as OLDER
/// (upgrade over it and warn), never as NEWER (which would block the install).
/// </para>
/// </remarks>
public static class VersionComparison
{
    /// <summary>
    /// Compare two version strings. Returns a value &lt; 0 when <paramref name="a"/>
    /// precedes <paramref name="b"/>, 0 when they are equal, and &gt; 0 when
    /// <paramref name="a"/> follows <paramref name="b"/>. When both parse as numeric
    /// dotted versions the compare is numeric; otherwise it is ordinal.
    /// </summary>
    public static int Compare(string? a, string? b)
    {
        if (Version.TryParse(a, out var va) && Version.TryParse(b, out var vb))
        {
            return va.CompareTo(vb);
        }

        return string.CompareOrdinal(a, b);
    }

    /// <summary>
    /// True when <paramref name="value"/> is a parseable numeric dotted version
    /// (what <see cref="System.Version"/> accepts). A SemVer pre-release tag or any
    /// other non-numeric form is <c>false</c> — see the pre-release note on this type.
    /// </summary>
    public static bool IsWellFormed(string? value) => Version.TryParse(value, out _);
}
