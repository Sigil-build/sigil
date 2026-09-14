// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using System.Collections.Generic;

namespace SigilBuild.Core.Manifest;

public sealed record BuildSection(
    string Source,
    IReadOnlyList<string>? Include,
    IReadOnlyList<string>? Exclude,
    bool Deterministic);
