namespace SigilBuild.Wrapper.Engine;

using SigilBuild.Wrapper.Cli;

/// <summary>
/// Immutable view over the resolved environment for a single install run.
/// Backs both expression evaluation (<c>When</c> clauses) and string
/// substitution inside step parameters via <see cref="Resolve"/>.
/// </summary>
public sealed class StepContext
{
    private const string PayloadScheme = "payload://";

    private readonly System.Collections.Generic.IReadOnlyDictionary<string, object?> _values;
    private readonly Expressions.Evaluator _evaluator = new();

    public StepContext(
        System.Collections.Generic.IReadOnlyDictionary<string, object?> values,
        string? payloadRoot = null)
    {
        System.ArgumentNullException.ThrowIfNull(values);
        _values = values;
        PayloadRoot = payloadRoot;
    }

    public static StepContext Empty { get; } =
        new StepContext(new System.Collections.Generic.Dictionary<string, object?>());

    /// <summary>
    /// Absolute path to the temp directory into which the embedded
    /// <c>SIGIL_PAYLOAD_V1</c> archive was extracted for this run, or
    /// <c>null</c> when the running exe carries no payload (an un-stamped dev
    /// runtime). Steps resolve <c>payload://relative/path</c> sources against
    /// it via <see cref="ResolvePath"/>. The directory's lifetime is owned by
    /// <see cref="InstallSession"/>, which deletes it once the run completes
    /// (on success, failure, cancel, or rollback).
    /// </summary>
    public string? PayloadRoot { get; }

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
    internal static StepContext From(WrapperBlob blob, ParsedCommandLine parsed, string? payloadRoot = null)
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

        // System context (used by the expression evaluator's `system.*` namespace).
        dict["system.os"] = System.Environment.OSVersion.Version.ToString();
        dict["system.arch"] = System.Runtime.InteropServices.RuntimeInformation
                                  .ProcessArchitecture.ToString().ToLowerInvariant();

        // Env context (only the well-known PATH for now; full env exposure is policy-deferred).
        dict["env.PATH"] = System.Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        return new StepContext(dict, payloadRoot);
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

    /// <summary>
    /// Resolve a path-valued step field: first expand <c>${...}</c> templates
    /// via <see cref="Resolve"/>, then — if the result begins with the
    /// <c>payload://</c> scheme — rebase the remainder onto
    /// <see cref="PayloadRoot"/> (the extracted embedded payload). Non-payload
    /// paths pass through unchanged, so every path-taking step can call this
    /// uniformly. A glob suffix (<c>payload://app/**</c>) survives the rebase
    /// and is interpreted by the step as usual.
    /// </summary>
    /// <exception cref="System.FormatException">
    /// A <c>payload://</c> path was used but no payload is available for this
    /// run, or the relative part escapes the payload root (a path-traversal
    /// attempt).
    /// </exception>
    public string ResolvePath(string template)
    {
        var resolved = Resolve(template);
        if (!resolved.StartsWith(PayloadScheme, System.StringComparison.Ordinal))
        {
            return resolved;
        }

        if (PayloadRoot is null)
        {
            throw new System.FormatException(
                string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"'payload://' source used but no payload was extracted for this run: '{template}'"));
        }

        var rel = resolved[PayloadScheme.Length..]
            .Replace('/', System.IO.Path.DirectorySeparatorChar)
            .Replace('\\', System.IO.Path.DirectorySeparatorChar)
            .TrimStart(System.IO.Path.DirectorySeparatorChar);

        var rootFull = System.IO.Path.GetFullPath(PayloadRoot);
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(rootFull, rel));

        // Guard against '..' traversal escaping the extracted payload root.
        var rootPrefix = rootFull.EndsWith(System.IO.Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + System.IO.Path.DirectorySeparatorChar;
        if (!string.Equals(full, rootFull, System.StringComparison.OrdinalIgnoreCase) &&
            !full.StartsWith(rootPrefix, System.StringComparison.OrdinalIgnoreCase))
        {
            throw new System.FormatException(
                string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"'payload://' source escapes the payload root: '{template}'"));
        }

        return full;
    }

    public bool Evaluate(string expression) => _evaluator.EvaluateBool(expression, _values);
}
