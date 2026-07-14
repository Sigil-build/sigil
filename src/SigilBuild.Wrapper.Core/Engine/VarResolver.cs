namespace SigilBuild.Wrapper.Engine;

using System;
using System.Collections.Generic;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Expressions;

/// <summary>
/// Evaluates the declarative <c>installer.vars</c> (P1) once at install-session
/// start and seeds <c>var.&lt;name&gt;</c> into the expression/substitution context.
/// Called by <see cref="StepContext.From"/> after every base identifier
/// (<c>param.*</c>, <c>app.*</c>, <c>system.*</c>, <c>env.*</c>, <c>scope</c>,
/// <c>install_dir</c>, <c>option.*</c>) has been seeded, so a var may reference any
/// of them plus earlier vars.
/// </summary>
/// <remarks>
/// Vars are evaluated in dependency order (<see cref="InstallerVarGraph"/>), so a
/// var is always resolved before any var that references it. Evaluation is total:
/// a var whose expression throws (unknown identifier, type error) resolves to
/// <c>""</c> rather than aborting the run — matching the "absent → empty string"
/// contract of the data-retrieval functions (ADR-008 §1.2). Secretness is
/// transitive (ADR-008 §3): a var whose expression references a secret parameter —
/// or a var already marked secret — is itself secret, its value registered for
/// redaction and its <c>var.&lt;name&gt;</c> identifier added to the taint set so
/// downstream vars inherit it.
/// </remarks>
internal static class VarResolver
{
    public static void Populate(
        IReadOnlyList<InstallerVar>? vars,
        Dictionary<string, object?> dict,
        Evaluator evaluator,
        ISet<string> secretIdentifiers,
        ICollection<string> secretValues)
    {
        ArgumentNullException.ThrowIfNull(dict);
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(secretIdentifiers);
        ArgumentNullException.ThrowIfNull(secretValues);

        if (vars is null || vars.Count == 0) return;

        var exprByName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var v in vars)
        {
            exprByName[v.Name] = v.Expression;
        }

        IReadOnlyList<string> order;
        try
        {
            order = InstallerVarGraph.TopologicalOrder(vars);
        }
        catch (Exception ex) when (ex is InstallerVarCycleException or ArgumentException)
        {
            // Cycles/duplicates are rejected at pack time (SIG0270). If a
            // hand-authored blob still carries one, fall back to declaration order
            // so the run never hangs — each var resolves against whatever is
            // already seeded ("" on the unresolved reference).
            var names = new List<string>(vars.Count);
            foreach (var v in vars)
            {
                if (!names.Contains(v.Name)) names.Add(v.Name);
            }
            order = names;
        }

        var secretVarNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var name in order)
        {
            var expr = exprByName[name];
            object? value;
            try
            {
                value = evaluator.EvaluateValue(expr, dict);
            }
            catch (ExpressionException)
            {
                value = string.Empty;
            }

            var key = "var." + name;
            dict[key] = value;

            if (ReferencesSecret(expr, secretIdentifiers, secretVarNames))
            {
                secretVarNames.Add(name);
                secretIdentifiers.Add(key); // taint the var identifier for downstream vars
                if (value is string s && s.Length > 0)
                {
                    secretValues.Add(s);
                }
            }
        }
    }

    private static bool ReferencesSecret(
        string expression, ISet<string> secretIdentifiers, HashSet<string> secretVarNames)
    {
        foreach (var id in InstallerVarGraph.ReferencedIdentifiers(expression))
        {
            if (secretIdentifiers.Contains(id))
            {
                return true;
            }

            if (id.Length > 4 && id.StartsWith("var.", StringComparison.Ordinal)
                && secretVarNames.Contains(id.Substring(4)))
            {
                return true;
            }
        }

        return false;
    }
}
