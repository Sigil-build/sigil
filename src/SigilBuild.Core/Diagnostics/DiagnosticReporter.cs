using System.Collections.Generic;
using System.IO;

namespace SigilBuild.Core.Diagnostics;

public static class DiagnosticReporter
{
    public static void Write(TextWriter writer, IEnumerable<Diagnostic> diagnostics, bool useColor)
    {
        foreach (var d in diagnostics)
        {
            var location = d.Location.File.Length == 0 ? "" : $"{d.Location} ";
            var sev = d.Severity switch
            {
                DiagnosticSeverity.Error => "error",
                DiagnosticSeverity.Warning => "warning",
                _ => "info",
            };
            writer.WriteLine($"{location}{sev} {d.Code}: {d.Message}");
            writer.WriteLine($"  see: {d.DocsUrl}");
        }
    }
}
