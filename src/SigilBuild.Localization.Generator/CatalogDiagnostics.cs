using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SigilBuild.Localization.Generator;

internal sealed class CatalogProblem
{
    public string Id { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string File { get; set; } = string.Empty;
    public int Line { get; set; }
}

internal static class CatalogValidator
{
    public static IReadOnlyList<CatalogProblem> Validate(IReadOnlyList<CatalogFile> files)
    {
        var problems = new List<CatalogProblem>();
        var en = files.FirstOrDefault(f => f.Lang == "en");

        foreach (var file in files)
        {
            var name = $"Strings.{file.Lang}.txt";

            foreach (var (message, line) in file.Malformed)
            {
                problems.Add(new CatalogProblem { Id = "SIGLOC005", Message = message, File = name, Line = line });
            }

            foreach (var group in file.Entries.GroupBy(e => e.Key, StringComparer.Ordinal).Where(g => g.Count() > 1))
            {
                problems.Add(new CatalogProblem
                {
                    Id = "SIGLOC004",
                    Message = $"duplicate key '{group.Key}'",
                    File = name,
                    Line = group.Last().Line,
                });
            }
        }

        if (en is null)
        {
            return problems;
        }

        // SIGLOC007 — a placeholder named for a reserved C# keyword compiles to
        // `string class` (a parameter) or a bare `class` identifier expression,
        // both CS1041. Contextual keywords (var, async, from, ...) are deliberately
        // NOT flagged: they are legal identifiers, so blocking them would be a false
        // positive with no matching compile failure. Scoped to en.Entries only — like
        // SIGLOC006, this must observe exactly what StringsEmitter.Emit iterates
        // (en.Entries), so a translation reusing the same keyword-named placeholder
        // as en does not produce a second, redundant diagnostic.
        foreach (var entry in en.Entries)
        {
            foreach (var placeholder in entry.Placeholders)
            {
                if (SyntaxFacts.GetKeywordKind(placeholder) != SyntaxKind.None)
                {
                    problems.Add(new CatalogProblem
                    {
                        Id = "SIGLOC007",
                        Message = $"placeholder '{{{placeholder}}}' in key '{entry.Key}' is a C# keyword and cannot be used as an identifier",
                        File = "Strings.en.txt",
                        Line = entry.Line,
                    });
                }
            }
        }

        // SIGLOC006 — the emitted `Strings` class has exactly one method per en.Entries
        // key (StringsEmitter.Emit iterates en.Entries only). Two distinct keys whose
        // MethodName collides would make the emitter write two identical signatures,
        // CS0111. Group on the generated name, not the key: that is the property that
        // actually breaks the build.
        foreach (var group in en.Entries
            .GroupBy(e => StringsEmitter.MethodName(e.Key), StringComparer.Ordinal)
            .Where(g => g.Select(e => e.Key).Distinct(StringComparer.Ordinal).Count() > 1))
        {
            var keys = string.Join(", ", group.Select(e => e.Key).Distinct(StringComparer.Ordinal).Select(k => $"'{k}'"));
            problems.Add(new CatalogProblem
            {
                Id = "SIGLOC006",
                Message = $"keys {keys} all generate method name '{group.Key}'",
                File = "Strings.en.txt",
                Line = group.Last().Line,
            });
        }

        // Not ToDictionary: en.Entries can itself contain a duplicate key (already reported
        // as SIGLOC004 above) and ToDictionary throws on that. Last-one-wins here; the
        // duplicate is already flagged, this dictionary only needs *a* representative entry.
        var enKeys = new Dictionary<string, CatalogEntry>(StringComparer.Ordinal);
        foreach (var e in en.Entries)
        {
            enKeys[e.Key] = e;
        }

        foreach (var file in files.Where(f => f.Lang != "en"))
        {
            var name = $"Strings.{file.Lang}.txt";

            foreach (var entry in file.Entries)
            {
                if (!enKeys.TryGetValue(entry.Key, out var enEntry))
                {
                    problems.Add(new CatalogProblem
                    {
                        Id = "SIGLOC001",
                        Message = $"key '{entry.Key}' has no counterpart in Strings.en.txt",
                        File = name,
                        Line = entry.Line,
                    });
                    continue;
                }

                // Set comparison, deliberately: a translation may reorder placeholders,
                // but must not drop or invent one.
                var enSet = new HashSet<string>(enEntry.Placeholders, StringComparer.Ordinal);
                var trSet = new HashSet<string>(entry.Placeholders, StringComparer.Ordinal);
                if (!enSet.SetEquals(trSet))
                {
                    problems.Add(new CatalogProblem
                    {
                        Id = "SIGLOC003",
                        Message =
                            $"key '{entry.Key}' placeholders {{{string.Join(",", trSet.OrderBy(x => x, StringComparer.Ordinal))}}} " +
                            $"do not match en {{{string.Join(",", enSet.OrderBy(x => x, StringComparer.Ordinal))}}}",
                        File = name,
                        Line = entry.Line,
                    });
                }
            }

            var trKeys = new HashSet<string>(file.Entries.Select(e => e.Key), StringComparer.Ordinal);
            foreach (var missing in en.Entries.Where(e => !trKeys.Contains(e.Key)))
            {
                problems.Add(new CatalogProblem
                {
                    Id = "SIGLOC002",
                    Message = $"key '{missing.Key}' is missing; falls back to English",
                    File = name,
                    Line = 1,
                });
            }
        }

        return problems;
    }

    public static readonly DiagnosticDescriptor Orphan = Descriptor("SIGLOC001", DiagnosticSeverity.Error);
    public static readonly DiagnosticDescriptor Missing = Descriptor("SIGLOC002", DiagnosticSeverity.Warning);
    public static readonly DiagnosticDescriptor Placeholders = Descriptor("SIGLOC003", DiagnosticSeverity.Error);
    public static readonly DiagnosticDescriptor Duplicate = Descriptor("SIGLOC004", DiagnosticSeverity.Error);
    public static readonly DiagnosticDescriptor Malformed = Descriptor("SIGLOC005", DiagnosticSeverity.Error);
    public static readonly DiagnosticDescriptor Collision = Descriptor("SIGLOC006", DiagnosticSeverity.Error);
    public static readonly DiagnosticDescriptor KeywordPlaceholder = Descriptor("SIGLOC007", DiagnosticSeverity.Error);

    public static DiagnosticDescriptor For(string id) => id switch
    {
        "SIGLOC001" => Orphan,
        "SIGLOC002" => Missing,
        "SIGLOC003" => Placeholders,
        "SIGLOC004" => Duplicate,
        "SIGLOC005" => Malformed,
        "SIGLOC006" => Collision,
        "SIGLOC007" => KeywordPlaceholder,
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, "unknown catalog diagnostic id"),
    };

    private static DiagnosticDescriptor Descriptor(string id, DiagnosticSeverity severity) =>
        new(id, "Localization catalog", "{0}", "Localization", severity, isEnabledByDefault: true);
}
