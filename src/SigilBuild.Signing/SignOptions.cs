// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

namespace SigilBuild.Signing;

public sealed record SignOptions(
    string ArtifactPath,
    bool ProduceDetachedSignature);
