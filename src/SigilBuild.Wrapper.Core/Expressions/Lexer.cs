using System.Collections.Generic;
using System.Globalization;

namespace SigilBuild.Wrapper.Expressions;

/// <summary>
/// Hand-rolled tokenizer for the <c>condition:</c> grammar. Produces a
/// flat token stream consumed by <see cref="Parser"/>. The lexer
/// recognizes a closed set of operators and keywords; anything else is
/// rejected with position info so the manifest author can see exactly
/// where the problem is.
///
/// Identifier syntax intentionally treats <c>parameters.edition</c> as a
/// single dotted path token (not <c>parameters</c> + <c>.</c> +
/// <c>edition</c>). This keeps the parser dead simple and lets the
/// evaluator look identifiers up by full key in the context dictionary
/// — there is no walk-the-property-graph step.
/// </summary>
internal static class Lexer
{
    public static IReadOnlyList<Token> Tokenize(string input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var tokens = new List<Token>();
        var i = 0;
        while (i < input.Length)
        {
            var c = input[i];

            // skip whitespace
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            // single-char punctuation
            switch (c)
            {
                case '(':
                    tokens.Add(new Token(TokenKind.LParen, "(", i));
                    i++;
                    continue;
                case ')':
                    tokens.Add(new Token(TokenKind.RParen, ")", i));
                    i++;
                    continue;
                case '[':
                    tokens.Add(new Token(TokenKind.LBracket, "[", i));
                    i++;
                    continue;
                case ']':
                    tokens.Add(new Token(TokenKind.RBracket, "]", i));
                    i++;
                    continue;
                case ',':
                    tokens.Add(new Token(TokenKind.Comma, ",", i));
                    i++;
                    continue;
            }

            // multi-char operators
            if (c == '=' && Peek(input, i + 1) == '=')
            {
                tokens.Add(new Token(TokenKind.Eq, "==", i));
                i += 2;
                continue;
            }

            if (c == '!' && Peek(input, i + 1) == '=')
            {
                tokens.Add(new Token(TokenKind.NotEq, "!=", i));
                i += 2;
                continue;
            }

            if (c == '<')
            {
                if (Peek(input, i + 1) == '=')
                {
                    tokens.Add(new Token(TokenKind.LtEq, "<=", i));
                    i += 2;
                }
                else
                {
                    tokens.Add(new Token(TokenKind.Lt, "<", i));
                    i++;
                }

                continue;
            }

            if (c == '>')
            {
                if (Peek(input, i + 1) == '=')
                {
                    tokens.Add(new Token(TokenKind.GtEq, ">=", i));
                    i += 2;
                }
                else
                {
                    tokens.Add(new Token(TokenKind.Gt, ">", i));
                    i++;
                }

                continue;
            }

            if (c == '&' && Peek(input, i + 1) == '&')
            {
                tokens.Add(new Token(TokenKind.And, "&&", i));
                i += 2;
                continue;
            }

            if (c == '|' && Peek(input, i + 1) == '|')
            {
                tokens.Add(new Token(TokenKind.Or, "||", i));
                i += 2;
                continue;
            }

            if (c == '!')
            {
                tokens.Add(new Token(TokenKind.Not, "!", i));
                i++;
                continue;
            }

            // string literals — single OR double quoted, NO escape sequences
            // (kept intentionally simple; if a manifest needs a quote in a
            // literal, alternate the quote style — `'O''Brien'` would not
            // work, but `"O'Brien"` does).
            if (c == '\'' || c == '"')
            {
                var start = i;
                var quote = c;
                i++;
                var sb = new System.Text.StringBuilder();
                while (i < input.Length && input[i] != quote)
                {
                    sb.Append(input[i]);
                    i++;
                }

                if (i >= input.Length)
                {
                    throw new ExpressionException(
                        $"unterminated string literal at position {start}");
                }

                i++; // consume closing quote
                tokens.Add(new Token(TokenKind.String, sb.ToString(), start));
                continue;
            }

            // integer literals
            if (char.IsAsciiDigit(c))
            {
                var start = i;
                while (i < input.Length && char.IsAsciiDigit(input[i]))
                {
                    i++;
                }

                tokens.Add(new Token(TokenKind.Integer, input.Substring(start, i - start), start));
                continue;
            }

            // identifiers (with dotted paths) and keywords
            if (IsIdentStart(c))
            {
                var start = i;
                while (i < input.Length && (IsIdentPart(input[i]) || input[i] == '.'))
                {
                    i++;
                }

                var lexeme = input.Substring(start, i - start);
                var kind = lexeme switch
                {
                    "true" => TokenKind.True,
                    "false" => TokenKind.False,
                    "in" => TokenKind.In,
                    "not_in" => TokenKind.NotIn,
                    _ => TokenKind.Identifier,
                };
                tokens.Add(new Token(kind, lexeme, start));
                continue;
            }

            throw new ExpressionException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"unexpected character '{c}' at position {i}"));
        }

        tokens.Add(new Token(TokenKind.EndOfInput, string.Empty, input.Length));
        return tokens;
    }

    private static char Peek(string input, int index) =>
        index < input.Length ? input[index] : '\0';

    private static bool IsIdentStart(char c) =>
        char.IsAsciiLetter(c) || c == '_';

    private static bool IsIdentPart(char c) =>
        char.IsAsciiLetterOrDigit(c) || c == '_';
}
