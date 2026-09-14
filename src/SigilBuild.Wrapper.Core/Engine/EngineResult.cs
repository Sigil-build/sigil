// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

namespace SigilBuild.Wrapper.Engine;

public sealed record EngineResult(bool Success, RollbackJournal Journal, string? Error)
{
    public static EngineResult Ok(RollbackJournal j) => new(true, j, null);
    public static EngineResult Failed(RollbackJournal j, string error) => new(false, j, error);
}
