using System;
using System.Collections.Generic;

namespace SigilBuild.Core.Manifest;

/// <summary>
/// Dependency analysis for <c>installer.vars</c> (P1). A var expression may
/// reference other vars via the <c>var.&lt;name&gt;</c> identifier, so the set of
/// vars forms a directed graph; the values must be evaluated in dependency order
/// (a referenced var before the var that references it), and a reference cycle is
/// an authoring error.
/// </summary>
/// <remarks>
/// This lives in <c>SigilBuild.Core</c> so both the manifest parser (pack-time
/// validation → <c>SIG0270</c> diagnostic) and the wrapper runtime (install-time
/// evaluation order) share one implementation. Because <c>SigilBuild.Core</c>
/// cannot reference the Wrapper.Core expression engine without a layering cycle,
/// references are extracted with a small string scan that mirrors the lexer's
/// identifier rules (dotted paths; contents of single/double-quoted string
/// literals are skipped so a literal like <c>"var.x"</c> is never a dependency).
/// The scan is intentionally conservative: it only needs to find <c>var.*</c> and
/// secret identifiers, not to fully parse the grammar.
/// </remarks>
public static class InstallerVarGraph
{
    /// <summary>
    /// Extract every dotted identifier path that appears in
    /// <paramref name="expression"/> outside of string literals (e.g.
    /// <c>param.token</c>, <c>var.old_path</c>, <c>app.id</c>). Function names are
    /// excluded (an identifier immediately followed by <c>(</c> is a call, not a
    /// value reference). Deterministic, allocation-light, no expression-engine
    /// dependency.
    /// </summary>
    public static IReadOnlyList<string> ReferencedIdentifiers(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);

        var result = new List<string>();
        var i = 0;
        var n = expression.Length;
        while (i < n)
        {
            var c = expression[i];

            // Skip string literals verbatim (single or double quoted, no escapes —
            // matches Lexer). An unterminated literal just runs to end-of-input.
            if (c is '\'' or '"')
            {
                var quote = c;
                i++;
                while (i < n && expression[i] != quote) i++;
                if (i < n) i++; // consume closing quote
                continue;
            }

            if (IsIdentStart(c))
            {
                var start = i;
                while (i < n && (IsIdentPart(expression[i]) || expression[i] == '.')) i++;

                // An identifier followed by '(' (after optional whitespace) is a
                // function call, not a value reference — skip it.
                var j = i;
                while (j < n && char.IsWhiteSpace(expression[j])) j++;
                if (j < n && expression[j] == '(')
                {
                    continue;
                }

                result.Add(expression.Substring(start, i - start));
                continue;
            }

            i++;
        }

        return result;
    }

    /// <summary>
    /// Return the var names in an order safe to evaluate in (each var after every
    /// var it references). Only references to <em>declared</em> vars constrain the
    /// order; references to params/app/system/etc. are ignored here (they are
    /// seeded before any var is evaluated).
    /// </summary>
    /// <exception cref="InstallerVarCycleException">
    /// A reference cycle (including a self-reference) exists among the vars.
    /// </exception>
    /// <exception cref="ArgumentException">Two vars share a name.</exception>
    public static IReadOnlyList<string> TopologicalOrder(IReadOnlyList<InstallerVar> vars)
    {
        ArgumentNullException.ThrowIfNull(vars);
        if (vars.Count == 0) return Array.Empty<string>();

        var exprByName = new Dictionary<string, string>(StringComparer.Ordinal);
        var declarationOrder = new List<string>(vars.Count);
        foreach (var v in vars)
        {
            if (!exprByName.TryAdd(v.Name, v.Expression))
            {
                throw new ArgumentException($"duplicate installer.vars entry '{v.Name}'", nameof(vars));
            }
            declarationOrder.Add(v.Name);
        }

        // Precompute each var's dependencies on OTHER declared vars, preserving
        // first-seen order for a deterministic result.
        var deps = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var name in declarationOrder)
        {
            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in ReferencedIdentifiers(exprByName[name]))
            {
                if (id.Length > 4 && id.StartsWith("var.", StringComparison.Ordinal))
                {
                    var target = id.Substring(4);
                    if (exprByName.ContainsKey(target) && seen.Add(target))
                    {
                        list.Add(target);
                    }
                }
            }
            deps[name] = list;
        }

        var ordered = new List<string>(vars.Count);
        var state = new Dictionary<string, Mark>(StringComparer.Ordinal);
        var stack = new List<string>();

        void Visit(string name)
        {
            state.TryGetValue(name, out var mark);
            if (mark == Mark.Done) return;
            if (mark == Mark.Visiting)
            {
                var cycleStart = stack.IndexOf(name);
                var cycle = stack.GetRange(cycleStart, stack.Count - cycleStart);
                cycle.Add(name);
                throw new InstallerVarCycleException(cycle);
            }

            state[name] = Mark.Visiting;
            stack.Add(name);
            foreach (var dep in deps[name])
            {
                Visit(dep);
            }
            stack.RemoveAt(stack.Count - 1);
            state[name] = Mark.Done;
            ordered.Add(name);
        }

        foreach (var name in declarationOrder)
        {
            Visit(name);
        }

        return ordered;
    }

    private static bool IsIdentStart(char c) => char.IsAsciiLetter(c) || c == '_';

    private static bool IsIdentPart(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

    private enum Mark
    {
        Unvisited = 0,
        Visiting,
        Done,
    }
}

/// <summary>
/// Thrown when <c>installer.vars</c> contain a reference cycle (a var whose
/// expression transitively references itself). The manifest parser converts this
/// into a <c>SIG0270</c> diagnostic; it should never escape at install time
/// because packing a cyclic manifest fails.
/// </summary>
public sealed class InstallerVarCycleException : Exception
{
    public InstallerVarCycleException(IReadOnlyList<string> cycle)
        : base("installer.vars contains a reference cycle: " + string.Join(" -> ", cycle))
    {
        Cycle = cycle;
    }

    /// <summary>The variable names forming the cycle, in order, ending where it began.</summary>
    public IReadOnlyList<string> Cycle { get; }
}
