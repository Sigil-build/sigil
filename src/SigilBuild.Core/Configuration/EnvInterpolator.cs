using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using SigilBuild.Core.Diagnostics;
using SigilBuild.Core.Manifest;

namespace SigilBuild.Core.Configuration;

public sealed record InterpolationResult(string Output, IReadOnlyList<Diagnostic> Diagnostics);

public static class EnvInterpolator
{
    private static readonly Regex Pattern = new(@"\$\$\{([A-Za-z_][A-Za-z0-9_]*)\}|\$\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled);

    public static InterpolationResult Expand(string input, IEnvironmentReader env)
    {
        var diagnostics = new List<Diagnostic>();
        var sb = new StringBuilder(input.Length);
        var lastIndex = 0;

        foreach (Match m in Pattern.Matches(input))
        {
            sb.Append(input, lastIndex, m.Index - lastIndex);
            // double-dollar escape: $${VAR} -> literal ${VAR}
            if (m.Groups[1].Success) { sb.Append("${").Append(m.Groups[1].Value).Append('}'); }
            else
            {
                var name = m.Groups[2].Value;
                var value = env.Get(name);
                if (value is null)
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticSeverity.Error,
                        DiagnosticCodes.EnvVariableMissing,
                        $"environment variable '{name}' is not set",
                        SourceLocation.Unknown,
                        "https://docs.sigil.build/diagnostics/SIG0020"));
                    sb.Append(m.Value);
                }
                else
                {
                    sb.Append(value);
                }
            }
            lastIndex = m.Index + m.Length;
        }
        sb.Append(input, lastIndex, input.Length - lastIndex);

        return new InterpolationResult(sb.ToString(), diagnostics);
    }
}
