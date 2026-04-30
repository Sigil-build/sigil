using System.Collections.Generic;
using System.IO;
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
}
