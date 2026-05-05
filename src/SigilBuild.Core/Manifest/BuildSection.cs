using System.Collections.Generic;

namespace SigilBuild.Core.Manifest;

public sealed record BuildSection(
    string Source,
    IReadOnlyList<string>? Include,
    IReadOnlyList<string>? Exclude,
    bool Deterministic);
