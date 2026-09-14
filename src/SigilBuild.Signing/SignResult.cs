// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using System.Collections.Generic;
using SigilBuild.Core.Diagnostics;

namespace SigilBuild.Signing;

public sealed record SignResult(
    bool Success,
    string? SignaturePath,
    string? Thumbprint,
    string? TimestampUrl,
    IReadOnlyList<Diagnostic> Diagnostics);
