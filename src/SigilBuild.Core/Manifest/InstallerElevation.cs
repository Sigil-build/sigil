// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

namespace SigilBuild.Core.Manifest;

/// <summary>
/// When an installer with a selectable scope asks Windows for administrator rights —
/// <c>installer.elevation</c>. Mirrors the schema enum exactly. (R118)
/// </summary>
/// <remarks>
/// Only meaningful for <c>scope: auto</c>. A <c>scope: machine</c> installer always
/// elevates at start, and <c>on_start</c> with <c>scope: user</c> is refused at pack
/// time (SIG0262): elevating a per-user install installs it into the profile of
/// whoever approved the prompt.
/// </remarks>
public enum InstallerElevation
{
    /// <summary>
    /// Default: elevate only when the run needs it — a machine scope resolved at start,
    /// or the wizard's "All users of this computer" choice, which hands the run to an
    /// elevated instance (R90, R117).
    /// </summary>
    OnDemand = 0,

    /// <summary>
    /// Elevate once at launch, whatever scope is chosen, so the wizard's scope radios
    /// switch freely without a second prompt or a relaunch.
    /// </summary>
    OnStart = 1,
}
