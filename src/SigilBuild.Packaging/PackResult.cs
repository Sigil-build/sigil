using System.Collections.Generic;
using SigilBuild.Core.Diagnostics;

namespace SigilBuild.Packaging;

public sealed record PackedArtifact(string Path, string Sha256, long SizeBytes);

public sealed record PackResult(
    PackedArtifact? Artifact,
    IReadOnlyList<Diagnostic> Diagnostics,
    // P12 (T12.5): populated only by a `--payload web` exe pack, alongside
    // Artifact (the full package hosted at PackageUrl) — the small stub whose
    // only install action downloads + runs that package. Null for every other
    // packager/format and for `--payload embedded`.
    PackedArtifact? SecondaryArtifact = null);
