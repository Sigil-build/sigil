using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SigilBuild.Core.Diagnostics;
using SigilBuild.Core.Manifest;

namespace SigilBuild.Core.Configuration;

public static class ManifestLoader
{
    public static async Task<LoadResult> LoadAsync(string path, IEnvironmentReader env)
    {
        if (!File.Exists(path))
        {
            return new LoadResult(null, new[]
            {
                new Diagnostic(DiagnosticSeverity.Error, DiagnosticCodes.FileNotFound,
                    $"manifest file '{path}' not found",
                    new SourceLocation(path, 0, 0),
                    "https://docs.sigil.build/diagnostics/SIG0002"),
            });
        }

        var raw = await File.ReadAllTextAsync(path);
        var interp = EnvInterpolator.Expand(raw, env);
        var diagnostics = new List<Diagnostic>(interp.Diagnostics);

        var schemaDiags = await SchemaValidator.ValidateAsync(interp.Output, path);
        diagnostics.AddRange(schemaDiags);

        var parsed = ManifestParser.Parse(interp.Output, path);
        diagnostics.AddRange(parsed.Diagnostics);

        var hasErrors = diagnostics.Exists(d => d.Severity == DiagnosticSeverity.Error);
        return new LoadResult(hasErrors ? null : parsed.Manifest, diagnostics);
    }

    /// <summary>
    /// Validate a manifest YAML string against the embedded schema and the typed-graph
    /// parser. Returns the union of schema violations and parser diagnostics.
    /// Does NOT perform environment-variable interpolation — pass an already-expanded
    /// YAML string, or use <see cref="LoadAsync"/> for the full pipeline.
    /// </summary>
    public static async Task<IReadOnlyList<Diagnostic>> ValidateAsync(string yaml, string fileName = "<inline>")
    {
        var diagnostics = new List<Diagnostic>();
        diagnostics.AddRange(await SchemaValidator.ValidateAsync(yaml, fileName));
        var parsed = ManifestParser.Parse(yaml, fileName);
        diagnostics.AddRange(parsed.Diagnostics);
        return diagnostics;
    }

    /// <summary>
    /// Validate a manifest YAML string and additionally apply per-parameter sample
    /// values (for example to check that a <c>pattern: "^[A-Z]{4}-[0-9]{4}$"</c>
    /// constraint accepts the candidate install-time input). Sample values are
    /// checked against the parameter's <c>pattern</c>, <c>min</c>, <c>max</c>,
    /// and (for <c>enum</c>) <c>values</c> constraints. Failures produce
    /// <c>SIG0220</c> diagnostics. Secret-typed parameter values are NEVER echoed
    /// back in diagnostic messages.
    /// </summary>
    public static async Task<IReadOnlyList<Diagnostic>> ValidateWithSampleValuesAsync(
        string yaml,
        IReadOnlyDictionary<string, string> sampleValues,
        string fileName = "<inline>")
    {
        var diagnostics = new List<Diagnostic>(await ValidateAsync(yaml, fileName));

        var parsed = ManifestParser.Parse(yaml, fileName);
        if (parsed.Manifest?.Parameters is null) return diagnostics;

        foreach (var (name, value) in sampleValues)
        {
            if (!parsed.Manifest.Parameters.TryGetValue(name, out var def)) continue;
            var failure = ValidateSampleValue(def, value);
            if (failure is null) continue;

            // Secret-aware redaction: never echo the raw value of a secret parameter.
            var displayValue = def.Type == ParameterType.Secret ? "***" : value;
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                DiagnosticCodes.ParameterValidationFailure,
                $"parameter '{name}' value {displayValue} {failure}",
                SourceLocation.Unknown,
                "https://docs.sigil.build/diagnostics/SIG0220"));
        }
        return diagnostics;
    }

    /// <summary>
    /// Per-call regex timeout. Pattern and value both come from user-controlled
    /// inputs (manifest author + install-time entry); a pathological pattern
    /// like <c>^(a+)+$</c> against a long value is a classic ReDoS. 1 second is
    /// generous enough that legitimate complex patterns finish, short enough
    /// that abuse fails fast.
    /// </summary>
    private static readonly System.TimeSpan RegexTimeout = System.TimeSpan.FromSeconds(1);

    private static string? ValidateSampleValue(ParameterDefinition def, string value)
    {
        if (def.Pattern is not null)
        {
            try
            {
                if (!Regex.IsMatch(value, def.Pattern, RegexOptions.None, RegexTimeout))
                    return $"does not match pattern '{def.Pattern}'";
            }
            catch (System.ArgumentException)
            {
                // Pattern itself is unparseable — surface as a parameter-level
                // failure instead of bubbling a stack trace to the caller.
                return $"has an unparseable pattern '{def.Pattern}'";
            }
            catch (RegexMatchTimeoutException)
            {
                return $"timed out matching pattern '{def.Pattern}' (possible ReDoS)";
            }
        }

        if (def.Type == ParameterType.Int)
        {
            if (!int.TryParse(value, out var n))
                return "is not an integer";
            if (def.Min is { } min && n < min)
                return $"is below minimum {min}";
            if (def.Max is { } max && n > max)
                return $"is above maximum {max}";
        }

        if (def.Type == ParameterType.Enum && def.EnumValues is { Count: > 0 } enumValues)
        {
            var allowed = false;
            foreach (var candidate in enumValues)
            {
                if (string.Equals(candidate, value, System.StringComparison.Ordinal))
                {
                    allowed = true;
                    break;
                }
            }
            if (!allowed)
                return $"is not one of [{string.Join(", ", enumValues)}]";
        }

        return null;
    }
}
