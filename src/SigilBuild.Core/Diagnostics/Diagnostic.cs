// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using SigilBuild.Core.Manifest;

namespace SigilBuild.Core.Diagnostics;

public sealed record Diagnostic(
    DiagnosticSeverity Severity,
    string Code,
    string Message,
    SourceLocation Location,
    string DocsUrl);
