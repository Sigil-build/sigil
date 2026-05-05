using System.Collections.Generic;
using SigilBuild.Core.Diagnostics;

namespace SigilBuild.Packaging;

public sealed record PackedArtifact(string Path, string Sha256, long SizeBytes);

public sealed record PackResult(
    PackedArtifact? Artifact,
    IReadOnlyList<Diagnostic> Diagnostics);
