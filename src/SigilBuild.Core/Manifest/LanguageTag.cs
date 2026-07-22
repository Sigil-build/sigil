namespace SigilBuild.Core.Manifest;

/// <summary>
/// The one language-tag rule, shared by pack-time validation (SIG0291) and the
/// installer's <c>/lang</c> flag. Two call sites, one implementation — see
/// docs/plan/feature-parity/P9-DESIGN-localization.md §6.2.
/// </summary>
/// <remarks>
/// A deliberate ordinal subset of BCP-47: <c>ALPHA{2,3} ( "-" ALPHANUM{1,8} )*</c>.
/// This accepts everything Sigil realistically needs (en, uk, pt-BR, zh-Hans,
/// de-AT) and rejects the malformed. Full BCP-47 — grandfathered tags,
/// extensions, private-use sequences — buys nothing here and would need a parser
/// the AOT constraints would rather not carry. No CultureInfo: constructing one
/// throws under InvariantGlobalization.
/// </remarks>
public static class LanguageTag
{
    public static bool IsValid(string? tag)
    {
        if (string.IsNullOrEmpty(tag))
        {
            return false;
        }

        var segments = tag!.Split('-');

        var primary = segments[0];
        if (primary.Length is < 2 or > 3 || !AllAlpha(primary))
        {
            return false;
        }

        for (var i = 1; i < segments.Length; i++)
        {
            var s = segments[i];
            if (s.Length is < 1 or > 8 || !AllAlphaNum(s))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AllAlpha(string s)
    {
        foreach (var c in s)
        {
            if (!IsAlpha(c)) return false;
        }
        return true;
    }

    private static bool AllAlphaNum(string s)
    {
        foreach (var c in s)
        {
            if (!IsAlpha(c) && !(c is >= '0' and <= '9')) return false;
        }
        return true;
    }

    // Ordinal ASCII checks only — char.IsLetter would drag in culture data.
    private static bool IsAlpha(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
}
