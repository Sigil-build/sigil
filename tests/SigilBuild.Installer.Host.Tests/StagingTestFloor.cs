// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using SigilBuild.Wrapper.Engine;

namespace SigilBuild.Installer.Host.Tests;

internal static class StagingTestFloor
{
    /// <summary>
    /// Never let this assembly take <c>SecureStaging</c>'s machine-wide (elevated) siting,
    /// whatever token the test host happens to hold.
    /// </summary>
    /// <remarks>
    /// CI runs <b>elevated</b>, and staging is reached transitively — the prerequisite
    /// runner, the update runner and every <c>{staging_dir}</c> resolution funnel into it —
    /// so without this floor a test that never mentions <c>SecureStaging</c> could create a
    /// directory in the real <c>%ProgramData%</c> and launch a binary out of it.
    /// </remarks>
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255", Justification = "Test-assembly bootstrap: pins staging away from the real %ProgramData% before any test runs.")]
    internal static void KeepStagingOutOfProgramData() => SecureStaging.NeverStageElevatedForTesting();
}
