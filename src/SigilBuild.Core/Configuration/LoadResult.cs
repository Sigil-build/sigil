using System.Collections.Generic;
using SigilBuild.Core.Diagnostics;
using SigilBuild.Core.Manifest;

namespace SigilBuild.Core.Configuration;

public sealed record LoadResult(SigilManifest? Manifest, IReadOnlyList<Diagnostic> Diagnostics);
