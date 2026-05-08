namespace SigilBuild.Wrapper.Expressions;

/// <summary>
/// Thrown for any error originating in the conditional expression engine —
/// lexing, parsing, evaluation, unknown identifier, unknown function, or
/// type mismatch. Callers should treat this as a *user-input* error
/// (bad <c>condition:</c> in a manifest), not an engine bug.
/// </summary>
public sealed class ExpressionException : System.Exception
{
    public ExpressionException(string message) : base(message) { }

    public ExpressionException(string message, System.Exception inner) : base(message, inner) { }
}
