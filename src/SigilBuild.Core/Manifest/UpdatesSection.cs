// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

namespace SigilBuild.Core.Manifest;

public sealed record UpdatesSection(
    string Channel,
    string? ManifestUrl,
    int DeltaTargets,
    string? SigningKey);
