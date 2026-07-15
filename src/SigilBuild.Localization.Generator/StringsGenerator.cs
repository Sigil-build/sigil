using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace SigilBuild.Localization.Generator;

/// <summary>
/// Incremental source generator that turns <c>Strings.&lt;lang&gt;.txt</c> catalog files
/// into a compiled-in string table (P9): parse (Task 1) -> validate (Task 3) -> emit
/// (Task 2). Emission is suppressed whenever any Error-severity <see cref="CatalogProblem"/>
/// is found — see the comment above <c>hasError</c> below for why.
/// </summary>
[Generator]
public sealed class StringsGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var catalogs = context.AdditionalTextsProvider
            .Where(static t => Path.GetFileName(t.Path).StartsWith("Strings.", System.StringComparison.Ordinal)
                            && Path.GetExtension(t.Path) == ".txt")
            .Select(static (t, ct) => (Name: Path.GetFileName(t.Path), Text: t.GetText(ct)?.ToString() ?? string.Empty))
            .Collect();

        context.RegisterSourceOutput(catalogs, static (spc, items) =>
        {
            if (items.IsDefaultOrEmpty)
            {
                return;
            }

            var files = items
                .Select(i => CatalogParser.Parse(i.Name, i.Text))
                .Where(f => f.Lang.Length > 0)
                .OrderBy(f => f.Lang, System.StringComparer.Ordinal)
                .ToList();

            var problems = CatalogValidator.Validate(files);
            var hasError = false;

            foreach (var problem in problems)
            {
                var descriptor = CatalogValidator.For(problem.Id);
                hasError |= descriptor.DefaultSeverity == DiagnosticSeverity.Error;

                spc.ReportDiagnostic(Diagnostic.Create(
                    descriptor,
                    Location.None,
                    $"{problem.File}({problem.Line}): {problem.Message}"));
            }

            // Emission is SUPPRESSED on any Error. Every Error-severity problem
            // corresponds to generated code that would also fail to compile with an
            // opaque error pointing at generated source (SIGLOC004/006 -> CS0111,
            // SIGLOC007 -> CS1041). Emitting anyway would show the author both, and
            // the opaque one is louder. Warnings (SIGLOC002 — a partial translation,
            // which is legal) must NOT suppress emission.
            if (hasError)
            {
                return;
            }

            spc.AddSource("Strings.g.cs", SourceText.From(StringsEmitter.Emit(files), System.Text.Encoding.UTF8));
        });
    }
}
