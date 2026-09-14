// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using System.Collections.Generic;
using SigilBuild.Core.Diagnostics;

namespace SigilBuild.Packaging;

public sealed record PackedArtifact(string Path, string Sha256, long SizeBytes);

public sealed record PackResult(
    PackedArtifact? Artifact,
    IReadOnlyList<Diagnostic> Diagnostics,
    // Populated only by a `--payload web` exe pack, alongside
    // Artifact (the full package hosted at PackageUrl) — the small stub whose
    // only install action downloads + runs that package. Null for every other
    // packager/format and for `--payload embedded`.
    PackedArtifact? SecondaryArtifact = null);
