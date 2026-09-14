// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

namespace SigilBuild.Wrapper.Engine;

internal interface IStep
{
    System.Threading.Tasks.Task<StepResult> RunAsync(
        StepContext ctx,
        RollbackJournal journal,
        System.Threading.CancellationToken ct);
}
