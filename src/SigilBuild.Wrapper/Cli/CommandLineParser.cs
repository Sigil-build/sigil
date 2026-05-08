namespace SigilBuild.Wrapper.Cli;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SigilBuild.Core.Manifest;

/// <summary>
/// Thrown when the install-time CLI receives unrecognized flags, undeclared
/// parameters, or otherwise malformed argument tokens. The message intentionally
/// names the offending token so end-users can correct typos without consulting
/// docs.
/// </summary>
public sealed class UsageException : Exception
{
    public UsageException(string message) : base(message) { }

    public UsageException() { }

    public UsageException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Operating mode for the wrapper, derived from the CLI flags.
/// </summary>
public enum WrapperMode
{
    /// <summary>Run the manifest's <c>install_steps</c> (default).</summary>
    Install,

    /// <summary>Run the manifest's <c>update_steps</c>; entered via <c>/Update</c> by the Update SDK.</summary>
    Update,

    /// <summary>Run the auto-derived uninstall sequence (Task 19); entered via <c>/Uninstall</c>.</summary>
    Uninstall,
}

/// <summary>
/// Result of parsing the wrapper's install-time argv. Captures the operating
/// mode, the silent-mode toggle, and the resolved parameter override values
/// keyed by their canonical schema-defined name.
/// </summary>
public sealed class ParsedCommandLine
{
    /// <summary>Operating mode requested by the user.</summary>
    public WrapperMode Mode { get; init; } = WrapperMode.Install;

    /// <summary>True if <c>/S</c> was present — suppresses interactive UI.</summary>
    public bool Silent { get; init; }

    /// <summary>
    /// Parameter overrides: schema canonical name → value. Lookups are
    /// case-insensitive but the keys themselves preserve schema casing so
    /// downstream consumers don't have to know about the input spelling.
    /// </summary>
    public IReadOnlyDictionary<string, string> Values { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Names (canonical casing) of the parameters whose schema type is <see cref="ParameterType.Secret"/>.</summary>
    public IReadOnlyList<string> SecretKeys { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Re-renders the parsed args as a single space-joined string, with secret
    /// parameter values replaced by <c>***</c>. Suitable for logging without
    /// leaking license keys / passwords / tokens.
    /// </summary>
    public string AuditSafeRendering()
    {
        var secretSet = new HashSet<string>(SecretKeys, StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();

        if (Silent)
        {
            sb.Append("/S");
        }

        switch (Mode)
        {
            case WrapperMode.Update:
                if (sb.Length > 0)
                {
                    sb.Append(' ');
                }
                sb.Append("/Update");
                break;
            case WrapperMode.Uninstall:
                if (sb.Length > 0)
                {
                    sb.Append(' ');
                }
                sb.Append("/Uninstall");
                break;
            case WrapperMode.Install:
            default:
                break;
        }

        foreach (var kv in Values)
        {
            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            sb.Append('/');
            sb.Append(kv.Key);
            sb.Append('=');
            sb.Append(secretSet.Contains(kv.Key) ? "***" : kv.Value);
        }

        return sb.ToString();
    }
}

/// <summary>
/// Closed-grammar parser for the wrapper's install-time CLI.
/// Recognized tokens (Windows installer convention):
/// <list type="bullet">
///   <item><description><c>/S</c> — silent mode.</description></item>
///   <item><description><c>/Update</c> — run <c>update_steps</c> instead of <c>install_steps</c>.</description></item>
///   <item><description><c>/Uninstall</c> — run the auto-derived uninstall sequence.</description></item>
///   <item><description><c>/&lt;Name&gt;=&lt;Value&gt;</c> — override a declared parameter.</description></item>
/// </list>
/// Anything else is a <see cref="UsageException"/> — the parser is intentionally
/// closed so silent typos never reach the step engine.
/// </summary>
public static class CommandLineParser
{
    /// <summary>
    /// Parse <paramref name="args"/> against the parameter <paramref name="schema"/>.
    /// </summary>
    /// <exception cref="UsageException">Unknown flag, undeclared parameter, or malformed token.</exception>
    /// <exception cref="ArgumentException">The schema itself is malformed (duplicate names).</exception>
    public static ParsedCommandLine Parse(IReadOnlyList<string> args, IReadOnlyList<ParameterDefinition> schema)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(schema);

        // Build a case-insensitive lookup over the schema, validating uniqueness.
        var byName = new Dictionary<string, ParameterDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var def in schema)
        {
            if (!byName.TryAdd(def.Name, def))
            {
                throw new ArgumentException(
                    $"duplicate parameter name '{def.Name}' in schema (case-insensitive)",
                    nameof(schema));
            }
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var mode = WrapperMode.Install;
        var silent = false;

        foreach (var rawArg in args)
        {
            if (string.IsNullOrEmpty(rawArg))
            {
                throw new UsageException("empty argument is not allowed");
            }

            if (rawArg[0] != '/')
            {
                throw new UsageException(
                    $"unexpected positional argument '{rawArg}': only /S, /Update, /Uninstall, and /Name=Value are accepted");
            }

            // Strip the leading '/'.
            var body = rawArg.Substring(1);

            // Bare flags first (case-insensitive, matches Windows installer convention).
            if (string.Equals(body, "S", StringComparison.OrdinalIgnoreCase))
            {
                silent = true;
                continue;
            }
            if (string.Equals(body, "Update", StringComparison.OrdinalIgnoreCase))
            {
                mode = WrapperMode.Update;
                continue;
            }
            if (string.Equals(body, "Uninstall", StringComparison.OrdinalIgnoreCase))
            {
                mode = WrapperMode.Uninstall;
                continue;
            }

            // /Name=Value form.
            var eq = body.IndexOf('=');
            if (eq <= 0)
            {
                throw new UsageException(
                    $"unrecognized flag '{rawArg}': expected /S, /Update, /Uninstall, or /Name=Value");
            }

            var inputName = body.Substring(0, eq);
            var value = body.Substring(eq + 1);

            if (!byName.TryGetValue(inputName, out var def))
            {
                throw new UsageException(
                    $"parameter '{inputName}' is not declared in the manifest (offending token: '{rawArg}')");
            }

            // Last-wins for duplicates. Preserve schema-canonical casing as the dictionary key.
            values[def.Name] = value;
        }

        var secretKeys = schema
            .Where(p => p.Type == ParameterType.Secret)
            .Select(p => p.Name)
            .ToArray();

        return new ParsedCommandLine
        {
            Mode = mode,
            Silent = silent,
            Values = values,
            SecretKeys = secretKeys,
        };
    }
}
