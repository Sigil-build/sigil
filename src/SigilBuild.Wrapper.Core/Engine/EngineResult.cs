// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

namespace SigilBuild.Wrapper.Engine;

public sealed record EngineResult(bool Success, RollbackJournal Journal, string? Error)
{
    /// <summary>
    /// True when there was nothing to uninstall: no uninstall state exists for the app in
    /// the requested scope. Never set for state that exists and was REFUSED — that is a
    /// failure, and reporting it as an absence would hide it (R1). Callers that present
    /// an uninstall treat this as "not installed", not as an error. (R107)
    /// </summary>
    public bool NotInstalled { get; init; }

    public static EngineResult Ok(RollbackJournal j) => new(true, j, null);
    public static EngineResult Failed(RollbackJournal j, string error) => new(false, j, error);

    /// <summary>The "no uninstall state exists" result; see <see cref="NotInstalled"/>. (R107)</summary>
    public static EngineResult Absent(RollbackJournal j, string message) =>
        new(false, j, message) { NotInstalled = true };
}
