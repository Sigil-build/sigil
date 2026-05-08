using System.Collections.Generic;
using SigilBuild.Core.Diagnostics;

namespace SigilBuild.Signing;

public sealed record SignResult(
    bool Success,
    string? SignaturePath,
    string? Thumbprint,
    string? TimestampUrl,
    IReadOnlyList<Diagnostic> Diagnostics);
