using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SigilBuild.Wrapper.Expressions;

/// <summary>
/// Public entry point for the conditional-expression engine. Parses an
/// expression and walks the resulting AST against a context dictionary
/// keyed by full identifier path (e.g. <c>"parameters.edition"</c>).
///
/// SECURITY MODEL (see ADR-008 — docs/architecture/adr-008-expression-policy.md §1):
/// <list type="bullet">
/// <item>Closed function table — see <see cref="Functions"/>.</item>
/// <item>Closed identifier set — anything not in <paramref name="context"/>
///       throws (except the <c>defined()</c> / <c>empty()</c>
///       special-case described below).</item>
/// <item>No reflection, no shell-out, no dynamic dispatch.</item>
/// </list>
/// </summary>
public sealed class Evaluator
{
    /// <summary>
    /// Evaluate an expression and require the result to be a boolean.
    /// </summary>
    public bool EvaluateBool(string expression, IReadOnlyDictionary<string, object?> context)
    {
        var value = EvaluateValue(expression, context);
        return value is bool b
            ? b
            : throw new ExpressionException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"expression did not evaluate to a boolean: {expression}"));
    }

    /// <summary>
    /// Evaluate an expression to its raw value (string / long / bool / list).
    /// Backs the <c>installer.vars</c> variable model (P1): a var's expression
    /// is evaluated once at session start and the result exposed as
    /// <c>var.&lt;name&gt;</c>. The same closed grammar, function table, and
    /// identifier set as <see cref="EvaluateBool"/> apply.
    /// </summary>
    public object? EvaluateValue(string expression, IReadOnlyDictionary<string, object?> context)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(context);

        var ast = Parser.Parse(expression);
        return Evaluate(ast, context);
    }

    private object? Evaluate(AstNode node, IReadOnlyDictionary<string, object?> ctx) =>
        node switch
        {
            AstNode.StringLit s => s.Value,
            AstNode.IntLit i => i.Value,
            AstNode.BoolLit b => b.Value,
            AstNode.Identifier id => ResolveIdentifier(id.Path, ctx),
            AstNode.FunctionCall fc => CallFunction(fc, ctx),
            AstNode.ListLit ll => ll.Elements.Select(e => Evaluate(e, ctx)).ToArray(),
            AstNode.Unary u => (object)ApplyUnary(u.Op, Evaluate(u.Operand, ctx)),
            AstNode.Binary b => (object)ApplyBinary(b.Op, b.Left, b.Right, ctx),
            _ => throw new ExpressionException($"unhandled AST node {node.GetType().Name}"),
        };

    private static object? ResolveIdentifier(string path, IReadOnlyDictionary<string, object?> ctx)
    {
        if (ctx.TryGetValue(path, out var v))
        {
            return v;
        }

        throw new ExpressionException($"unknown identifier '{path}'");
    }

    private object? CallFunction(AstNode.FunctionCall fc, IReadOnlyDictionary<string, object?> ctx)
    {
        if (!Functions.Table.TryGetValue(fc.Name, out var impl))
        {
            throw new ExpressionException($"unknown function '{fc.Name}'");
        }

        // Special-case: defined() / empty() should observe a missing
        // identifier as "absent" (null), not bubble up a hard parse-style
        // error. Other ExpressionExceptions still propagate so a buggy
        // expression like `defined(unknown_func(x))` is not silently
        // swallowed. This is the cleaner of the two options in the task
        // brief — no need to weaken identifier resolution globally.
        var swallowUnknownIdent = fc.Name is "defined" or "empty";

        var args = new object?[fc.Args.Count];
        for (var i = 0; i < fc.Args.Count; i++)
        {
            try
            {
                args[i] = Evaluate(fc.Args[i], ctx);
            }
            catch (ExpressionException ex) when (swallowUnknownIdent
                                                 && ex.Message.StartsWith("unknown identifier", StringComparison.Ordinal))
            {
                args[i] = null;
            }
        }

        return impl(args);
    }

    // Returns bool — every unary operator we support is `!`, which is
    // strictly boolean. Concrete return type satisfies CA1859.
    private static bool ApplyUnary(string op, object? operand) => op switch
    {
        "!" => operand is bool b
            ? !b
            : throw new ExpressionException($"operator '!' requires a boolean operand, got {Describe(operand)}"),
        _ => throw new ExpressionException($"unknown unary operator '{op}'"),
    };

    // ApplyBinary takes the original left/right AST nodes so we can
    // short-circuit && and || without evaluating the right operand when
    // the left side already determines the result. For all other
    // operators we eagerly evaluate both sides up front.
    //
    // Every binary op in the closed grammar yields a boolean (==, !=, <,
    // <=, >, >=, &&, ||, in, not_in), so the concrete return type is
    // bool — satisfies CA1859.
    private bool ApplyBinary(string op, AstNode leftNode, AstNode rightNode, IReadOnlyDictionary<string, object?> ctx)
    {
        switch (op)
        {
            case "&&":
            {
                var l = Evaluate(leftNode, ctx);
                if (l is not bool lb)
                {
                    throw new ExpressionException($"operator '&&' requires boolean operands, got {Describe(l)}");
                }

                if (!lb)
                {
                    return false;
                }

                var r = Evaluate(rightNode, ctx);
                return r is bool rb
                    ? rb
                    : throw new ExpressionException($"operator '&&' requires boolean operands, got {Describe(r)}");
            }

            case "||":
            {
                var l = Evaluate(leftNode, ctx);
                if (l is not bool lb)
                {
                    throw new ExpressionException($"operator '||' requires boolean operands, got {Describe(l)}");
                }

                if (lb)
                {
                    return true;
                }

                var r = Evaluate(rightNode, ctx);
                return r is bool rb
                    ? rb
                    : throw new ExpressionException($"operator '||' requires boolean operands, got {Describe(r)}");
            }
        }

        var left = Evaluate(leftNode, ctx);
        var right = Evaluate(rightNode, ctx);

        return op switch
        {
            "==" => Equal(left, right),
            "!=" => !Equal(left, right),
            "<" => Compare(left, right) < 0,
            "<=" => Compare(left, right) <= 0,
            ">" => Compare(left, right) > 0,
            ">=" => Compare(left, right) >= 0,
            "in" => InList(left, right, expected: true),
            "not_in" => InList(left, right, expected: false),
            _ => throw new ExpressionException($"unknown binary operator '{op}'"),
        };
    }

    private static bool Equal(object? a, object? b)
    {
        var (x, y) = NormalizeNumeric(a, b);
        return Equals(x, y);
    }

    // Comparison ops require like-with-like (after int↔long normalization).
    // Mismatched types throw rather than silently coerce — manifests should
    // be explicit, and a typo like `parameters.edition < 5` should fail
    // loudly at runtime with a clear message.
    private static int Compare(object? a, object? b)
    {
        var (x, y) = NormalizeNumeric(a, b);
        if (x is null || y is null)
        {
            throw new ExpressionException("cannot compare null with non-null");
        }

        if (x is long lx && y is long ly)
        {
            return lx.CompareTo(ly);
        }

        if (x is string sx && y is string sy)
        {
            return string.CompareOrdinal(sx, sy);
        }

        if (x is bool bx && y is bool by)
        {
            return bx.CompareTo(by);
        }

        throw new ExpressionException(
            $"cannot compare {Describe(a)} with {Describe(b)}");
    }

    private static bool InList(object? needle, object? haystack, bool expected)
    {
        if (haystack is not object?[] arr)
        {
            throw new ExpressionException(
                $"'in'/'not_in' requires a list on the right-hand side, got {Describe(haystack)}");
        }

        foreach (var elem in arr)
        {
            if (Equal(needle, elem))
            {
                return expected;
            }
        }

        return !expected;
    }

    // Normalize int (which the parser doesn't produce, but the context
    // dictionary might) and long to a common long. Anything else passes
    // through unchanged.
    private static (object? Left, object? Right) NormalizeNumeric(object? a, object? b)
    {
        var na = a is int ia ? (long)ia : a;
        var nb = b is int ib ? (long)ib : b;
        return (na, nb);
    }

    private static string Describe(object? value) => value switch
    {
        null => "null",
        string => "string",
        long or int => "integer",
        bool => "boolean",
        ICollection => "list",
        _ => value.GetType().Name,
    };
}
