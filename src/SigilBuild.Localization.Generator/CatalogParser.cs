using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace SigilBuild.Localization.Generator;

internal sealed class CatalogEntry
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public IReadOnlyList<string> Placeholders { get; set; } = Array.Empty<string>();
    public int Line { get; set; }
}

internal sealed class CatalogFile
{
    public string Lang { get; set; } = string.Empty;
    public IReadOnlyList<CatalogEntry> Entries { get; set; } = Array.Empty<CatalogEntry>();
    public IReadOnlyList<(string Message, int Line)> Malformed { get; set; }
        = Array.Empty<(string, int)>();
}

internal static class CatalogParser
{
    private static readonly Regex PlaceholderPattern = new(@"\{([A-Za-z][A-Za-z0-9]*)\}", RegexOptions.Compiled);
    private static readonly Regex PlaceholderNamePattern = new(@"^[A-Za-z][A-Za-z0-9]*$", RegexOptions.Compiled);
    private static readonly Regex FileNamePattern = new(@"^Strings\.([A-Za-z0-9-]+)\.txt$", RegexOptions.Compiled);

    /// <summary>
    /// The single definition of "is this a placeholder name". StringsEmitter must use this
    /// (not its own copy) so that a <c>{...}</c> span the parser did not register as a
    /// placeholder is never emitted as a bare identifier — that's a straight CS0103.
    /// </summary>
    public static bool IsPlaceholderName(string name) => PlaceholderNamePattern.IsMatch(name);

    public static CatalogFile Parse(string fileName, string text)
    {
        var langMatch = FileNamePattern.Match(fileName);
        var entries = new List<CatalogEntry>();
        var malformed = new List<(string, int)>();

        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq < 0)
            {
                malformed.Add(($"expected 'key = value', got '{line}'", i + 1));
                continue;
            }

            var key = line.Substring(0, eq).Trim();
            var value = Unescape(line.Substring(eq + 1).Trim());
            if (key.Length == 0)
            {
                malformed.Add(("key is empty", i + 1));
                continue;
            }

            var names = new List<string>();
            foreach (Match m in PlaceholderPattern.Matches(value))
            {
                names.Add(m.Groups[1].Value);
            }

            entries.Add(new CatalogEntry { Key = key, Value = value, Placeholders = names, Line = i + 1 });
        }

        return new CatalogFile
        {
            Lang = langMatch.Success ? langMatch.Groups[1].Value : string.Empty,
            Entries = entries,
            Malformed = malformed,
        };
    }

    /// <summary>
    /// Interprets the catalog's own escape syntax so <see cref="CatalogEntry.Value"/> holds
    /// the real string the author meant. A literal two-character <c>\n</c> becomes a real
    /// line-feed (the catalog .txt format has no other way to express a line break); a
    /// literal <c>\\</c> becomes a single literal backslash, so an author who genuinely wants
    /// the two characters <c>\n</c> to appear can write <c>\\n</c>. No other escapes exist —
    /// YAGNI. StringsEmitter.Quote() then re-escapes a real line-feed back into the C# source
    /// escape <c>\n</c>, and escapes backslashes first, so this round-trips without double-escaping.
    /// </summary>
    private static string Unescape(string value)
    {
        if (value.IndexOf('\\') < 0)
        {
            return value;
        }

        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length && (value[i + 1] == 'n' || value[i + 1] == '\\'))
            {
                sb.Append(value[i + 1] == 'n' ? '\n' : '\\');
                i++;
                continue;
            }
            sb.Append(value[i]);
        }
        return sb.ToString();
    }
}
