// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using System.Threading;
using System.Threading.Tasks;
using SigilBuild.Core.Manifest;

namespace SigilBuild.Packaging;

public interface IPackager
{
    PackageFormat Format { get; }
    Task<PackResult> PackAsync(SigilManifest manifest, PackOptions options, CancellationToken ct);
}
