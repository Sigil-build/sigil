// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using System.Threading;
using System.Threading.Tasks;

namespace SigilBuild.Signing;

public interface ISigningProvider
{
    string Name { get; }
    Task<SignResult> SignAsync(SignOptions options, CancellationToken ct);
}
