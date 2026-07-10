namespace SigilBuild.Wrapper.Expressions;

internal enum TokenKind
{
    // Literals
    Identifier,
    String,
    Integer,
    True,
    False,

    // Operators
    Eq,
    NotEq,
    Lt,
    LtEq,
    Gt,
    GtEq,
    And,
    Or,
    Not,
    In,
    NotIn,

    // Punctuation
    LParen,
    RParen,
    LBracket,
    RBracket,
    Comma,
    Dot,

    // Sentinels
    EndOfInput,
}

internal readonly record struct Token(TokenKind Kind, string Lexeme, int Position);
