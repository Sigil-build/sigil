using System.Collections.Generic;
using System.Text;

namespace SigilBuild.Localization.Generator;

/// <summary>
/// Transforms an English value into its Lang.Pseudo form: accented letters plus
/// bracket sentinels. The pseudo-loc test asserts every rendered UI string is
/// bracketed, so any plain-ASCII text on screen is a hardcoded string.
/// Placeholders are copied through untouched — they are code, not text.
/// </summary>
internal static class PseudoTransform
{
    private static readonly Dictionary<char, char> Map = new()
    {
        ['a'] = 'à',
        ['b'] = 'Ƀ',
        ['c'] = 'ç',
        ['d'] = 'ď',
        ['e'] = 'è',
        ['g'] = 'ģ',
        ['i'] = 'ï',
        ['k'] = 'ķ',
        ['l'] = 'ĺ',
        ['n'] = 'ñ',
        ['o'] = 'ò',
        ['s'] = 'š',
        ['t'] = 'ť',
        ['u'] = 'ü',
        ['y'] = 'ý',
        ['z'] = 'ž',
        ['A'] = 'À',
        ['C'] = 'Ç',
        ['E'] = 'È',
        ['I'] = 'Ï',
        ['N'] = 'Ñ',
        ['O'] = 'Ò',
        ['S'] = 'Š',
        ['U'] = 'Ü',
    };

    public static string Apply(string value)
    {
        var sb = new StringBuilder("[");
        var inPlaceholder = false;

        foreach (var c in value)
        {
            if (c == '{') inPlaceholder = true;
            if (c == '}') { inPlaceholder = false; sb.Append(c); continue; }

            sb.Append(!inPlaceholder && Map.TryGetValue(c, out var mapped) ? mapped : c);
        }

        sb.Append("‼]");
        return sb.ToString();
    }
}
