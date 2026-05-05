using SigilBuild.Core.Manifest;

namespace SigilBuild.Core.Diagnostics;

public sealed record Diagnostic(
    DiagnosticSeverity Severity,
    string Code,
    string Message,
    SourceLocation Location,
    string DocsUrl);
