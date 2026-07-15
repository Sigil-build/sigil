using System;
using System.Collections.Generic;
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
    private static readonly Regex FileNamePattern = new(@"^Strings\.([A-Za-z0-9-]+)\.txt$", RegexOptions.Compiled);

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
            if (eq <= 0)
            {
                malformed.Add(($"expected 'key = value', got '{line}'", i + 1));
                continue;
            }

            var key = line.Substring(0, eq).Trim();
            var value = line.Substring(eq + 1).Trim();
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
}
