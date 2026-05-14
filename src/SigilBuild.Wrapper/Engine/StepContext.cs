namespace SigilBuild.Wrapper.Engine;

using SigilBuild.Wrapper.Cli;

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

    /// <summary>
    /// Build a <see cref="StepContext"/> by materializing parameter overrides
    /// from <paramref name="parsed"/> against the schema in <paramref name="blob"/>,
    /// then layering the <c>system.*</c> and <c>env.*</c> namespaces used by
    /// the expression evaluator's <c>When</c> clauses and by
    /// <see cref="Resolve"/> templates.
    /// </summary>
    /// <remarks>
    /// Resolution precedence for each declared parameter is
    /// CLI override → schema default → <c>null</c>. Undeclared CLI params
    /// can never reach this method — <see cref="CommandLineParser.Parse"/>
    /// rejects them up-front.
    /// </remarks>
    internal static StepContext From(WrapperBlob blob, ParsedCommandLine parsed)
    {
        System.ArgumentNullException.ThrowIfNull(blob);
        System.ArgumentNullException.ThrowIfNull(parsed);

        var dict = new System.Collections.Generic.Dictionary<string, object?>(System.StringComparer.Ordinal);

        // Materialise parameter values: CLI override → schema default → null.
        foreach (var def in blob.Parameters)
        {
            var key = "parameters." + def.Name;
            if (parsed.Values.TryGetValue(def.Name, out var v))
            {
                dict[key] = v;
            }
            else if (def.Default is not null)
            {
                dict[key] = def.Default;
            }
            else
            {
                dict[key] = null;
            }
        }

        // App metadata — sourced from the manifest's `app:` block via
        // WrapperBlob.App. Without these, ${app.version} / ${app.publisher}
        // etc. in registry_write values land as literal placeholder text in
        // the registry, which the user cannot tell from a real failure.
        dict["app.id"] = blob.App.Id;
        dict["app.name"] = blob.App.Name;
        dict["app.version"] = blob.App.Version;
        dict["app.publisher"] = blob.App.Publisher;
        dict["app.description"] = blob.App.Description ?? string.Empty;
        dict["app.homepage"] = blob.App.Homepage ?? string.Empty;

        // System context (used by the expression evaluator's `system.*` namespace).
        dict["system.os"] = System.Environment.OSVersion.Version.ToString();
        dict["system.arch"] = System.Runtime.InteropServices.RuntimeInformation
                                  .ProcessArchitecture.ToString().ToLowerInvariant();

        // Env context (only the well-known PATH for now; full env exposure is policy-deferred).
        dict["env.PATH"] = System.Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        return new StepContext(dict);
    }

    /// <summary>
    /// Look up a raw context value by its full identifier path
    /// (e.g. <c>"parameters.install_dir"</c>, <c>"app.version"</c>). Returns
    /// <c>false</c> when the key is unknown — used by code paths that need a
    /// soft "fetch if present" semantic without the <see cref="Resolve"/>
    /// failure mode (which throws <see cref="System.FormatException"/> on a
    /// missing identifier).
    /// </summary>
    public bool TryGet(string path, out object? value)
    {
        System.ArgumentNullException.ThrowIfNull(path);
        return _values.TryGetValue(path, out value);
    }

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
