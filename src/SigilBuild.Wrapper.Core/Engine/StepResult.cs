// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

namespace SigilBuild.Wrapper.Engine;

public sealed record StepResult(bool Success, string? Error)
{
    public static StepResult Ok() => new(true, null);
    public static StepResult Failed(string error) => new(false, error);
}
