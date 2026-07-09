using System.Collections.Generic;

namespace SigilBuild.Wrapper.Expressions;

/// <summary>
/// Discriminated AST union for the closed expression grammar. Concrete
/// nodes are nested records so the type switch in
/// <see cref="Evaluator"/> stays exhaustive at the C# language level.
/// </summary>
internal abstract record AstNode
{
    internal sealed record StringLit(string Value) : AstNode;

    internal sealed record IntLit(long Value) : AstNode;

    internal sealed record BoolLit(bool Value) : AstNode;

    internal sealed record Identifier(string Path) : AstNode;

    internal sealed record FunctionCall(string Name, IReadOnlyList<AstNode> Args) : AstNode;

    internal sealed record ListLit(IReadOnlyList<AstNode> Elements) : AstNode;

    internal sealed record Unary(string Op, AstNode Operand) : AstNode;

    internal sealed record Binary(string Op, AstNode Left, AstNode Right) : AstNode;
}
