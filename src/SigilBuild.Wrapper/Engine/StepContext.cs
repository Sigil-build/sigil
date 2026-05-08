namespace SigilBuild.Wrapper.Engine;

/// <summary>
/// Immutable view over the resolved environment for a single install run.
/// Backs both expression evaluation (<c>When</c> clauses) and string
/// substitution inside step parameters via <see cref="Resolve"/>.
/// </summary>
public sealed class StepContext
{
    private readonly System.Collections.Generic.IReadOnlyDictionary<string, object?> _values;
    private readonly Expressions.Evaluator _evaluator = new();

    public StepContext(System.Collections.Generic.IReadOnlyDictionary<string, object?> values)
    {
        System.ArgumentNullException.ThrowIfNull(values);
        _values = values;
    }

    public static StepContext Empty { get; } =
        new StepContext(new System.Collections.Generic.Dictionary<string, object?>());

    /// <summary>Substitute <c>${parameters.foo}</c> patterns in <paramref name="template"/>.</summary>
    public string Resolve(string template)
    {
        if (string.IsNullOrEmpty(template))
        {
            return template;
        }

        // Simple ${path} substitution; no recursion, no defaults.
        var sb = new System.Text.StringBuilder(template.Length);
        var i = 0;
        while (i < template.Length)
        {
            if (template[i] == '$' && i + 1 < template.Length && template[i + 1] == '{')
            {
                var end = template.IndexOf('}', i + 2);
                if (end < 0)
                {
                    throw new System.FormatException("unterminated ${...} in template");
                }

                var path = template.Substring(i + 2, end - i - 2);
                if (!_values.TryGetValue(path, out var v))
                {
                    throw new System.FormatException(
                        string.Create(
                            System.Globalization.CultureInfo.InvariantCulture,
                            $"unknown identifier '{path}' in template"));
                }

                sb.Append(v?.ToString() ?? string.Empty);
                i = end + 1;
            }
            else
            {
                sb.Append(template[i]);
                i++;
            }
        }
        return sb.ToString();
    }

    public bool Evaluate(string expression) => _evaluator.EvaluateBool(expression, _values);
}
