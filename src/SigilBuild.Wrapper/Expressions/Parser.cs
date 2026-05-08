using System.Collections.Generic;
using System.Globalization;

namespace SigilBuild.Wrapper.Expressions;

/// <summary>
/// Pratt-style precedence parser. Operator precedence (low → high):
/// <list type="number">
///   <item><c>||</c></item>
///   <item><c>&amp;&amp;</c></item>
///   <item><c>in</c> / <c>not_in</c></item>
///   <item><c>==</c> / <c>!=</c></item>
///   <item><c>&lt;</c> / <c>&lt;=</c> / <c>&gt;</c> / <c>&gt;=</c></item>
///   <item>unary <c>!</c></item>
///   <item>primary: literal, identifier, function call, parenthesized expression, list literal</item>
/// </list>
///
/// Each precedence level is a method that calls the next-higher level for
/// its operands. This is conventional recursive-descent rather than a
/// table-driven Pratt parser, but the precedence layering matches what a
/// classic Pratt parser would produce, which is what the spec asks for.
/// </summary>
internal static class Parser
{
    public static AstNode Parse(string input)
    {
        var tokens = Lexer.Tokenize(input);
        var pos = 0;
        var node = ParseOr(tokens, ref pos);
        if (tokens[pos].Kind != TokenKind.EndOfInput)
        {
            throw new ExpressionException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"unexpected token '{tokens[pos].Lexeme}' at position {tokens[pos].Position}"));
        }

        return node;
    }

    // 1. ||
    private static AstNode ParseOr(IReadOnlyList<Token> tokens, ref int pos)
    {
        var left = ParseAnd(tokens, ref pos);
        while (tokens[pos].Kind == TokenKind.Or)
        {
            pos++;
            var right = ParseAnd(tokens, ref pos);
            left = new AstNode.Binary("||", left, right);
        }

        return left;
    }

    // 2. &&
    private static AstNode ParseAnd(IReadOnlyList<Token> tokens, ref int pos)
    {
        var left = ParseInOp(tokens, ref pos);
        while (tokens[pos].Kind == TokenKind.And)
        {
            pos++;
            var right = ParseInOp(tokens, ref pos);
            left = new AstNode.Binary("&&", left, right);
        }

        return left;
    }

    // 3. in / not_in
    private static AstNode ParseInOp(IReadOnlyList<Token> tokens, ref int pos)
    {
        var left = ParseEquality(tokens, ref pos);
        while (tokens[pos].Kind is TokenKind.In or TokenKind.NotIn)
        {
            var op = tokens[pos].Kind == TokenKind.In ? "in" : "not_in";
            pos++;
            var right = ParseEquality(tokens, ref pos);
            left = new AstNode.Binary(op, left, right);
        }

        return left;
    }

    // 4. == / !=
    private static AstNode ParseEquality(IReadOnlyList<Token> tokens, ref int pos)
    {
        var left = ParseRelational(tokens, ref pos);
        while (tokens[pos].Kind is TokenKind.Eq or TokenKind.NotEq)
        {
            var op = tokens[pos].Kind == TokenKind.Eq ? "==" : "!=";
            pos++;
            var right = ParseRelational(tokens, ref pos);
            left = new AstNode.Binary(op, left, right);
        }

        return left;
    }

    // 5. < <= > >=
    private static AstNode ParseRelational(IReadOnlyList<Token> tokens, ref int pos)
    {
        var left = ParseUnary(tokens, ref pos);
        while (tokens[pos].Kind is TokenKind.Lt or TokenKind.LtEq or TokenKind.Gt or TokenKind.GtEq)
        {
            var op = tokens[pos].Kind switch
            {
                TokenKind.Lt => "<",
                TokenKind.LtEq => "<=",
                TokenKind.Gt => ">",
                _ => ">=",
            };
            pos++;
            var right = ParseUnary(tokens, ref pos);
            left = new AstNode.Binary(op, left, right);
        }

        return left;
    }

    // 6. unary !
    private static AstNode ParseUnary(IReadOnlyList<Token> tokens, ref int pos)
    {
        if (tokens[pos].Kind == TokenKind.Not)
        {
            pos++;
            var operand = ParseUnary(tokens, ref pos);
            return new AstNode.Unary("!", operand);
        }

        return ParsePrimary(tokens, ref pos);
    }

    // 7. primary
    private static AstNode ParsePrimary(IReadOnlyList<Token> tokens, ref int pos)
    {
        var t = tokens[pos];
        switch (t.Kind)
        {
            case TokenKind.String:
                pos++;
                return new AstNode.StringLit(t.Lexeme);

            case TokenKind.Integer:
                pos++;
                if (!long.TryParse(t.Lexeme, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                {
                    throw new ExpressionException(
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"invalid integer literal '{t.Lexeme}' at position {t.Position}"));
                }

                return new AstNode.IntLit(n);

            case TokenKind.True:
                pos++;
                return new AstNode.BoolLit(true);

            case TokenKind.False:
                pos++;
                return new AstNode.BoolLit(false);

            case TokenKind.LParen:
                pos++;
                var inner = ParseOr(tokens, ref pos);
                Expect(tokens, ref pos, TokenKind.RParen, ")");
                return inner;

            case TokenKind.LBracket:
                pos++;
                var elements = new List<AstNode>();
                if (tokens[pos].Kind != TokenKind.RBracket)
                {
                    elements.Add(ParseOr(tokens, ref pos));
                    while (tokens[pos].Kind == TokenKind.Comma)
                    {
                        pos++;
                        elements.Add(ParseOr(tokens, ref pos));
                    }
                }

                Expect(tokens, ref pos, TokenKind.RBracket, "]");
                return new AstNode.ListLit(elements);

            case TokenKind.Identifier:
                pos++;
                if (tokens[pos].Kind == TokenKind.LParen)
                {
                    // function call
                    pos++;
                    var args = new List<AstNode>();
                    if (tokens[pos].Kind != TokenKind.RParen)
                    {
                        args.Add(ParseOr(tokens, ref pos));
                        while (tokens[pos].Kind == TokenKind.Comma)
                        {
                            pos++;
                            args.Add(ParseOr(tokens, ref pos));
                        }
                    }

                    Expect(tokens, ref pos, TokenKind.RParen, ")");
                    return new AstNode.FunctionCall(t.Lexeme, args);
                }

                return new AstNode.Identifier(t.Lexeme);

            default:
                throw new ExpressionException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"unexpected token '{t.Lexeme}' at position {t.Position}"));
        }
    }

    private static void Expect(IReadOnlyList<Token> tokens, ref int pos, TokenKind kind, string lexeme)
    {
        if (tokens[pos].Kind != kind)
        {
            throw new ExpressionException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"expected '{lexeme}' but found '{tokens[pos].Lexeme}' at position {tokens[pos].Position}"));
        }

        pos++;
    }
}
