// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using SigilBuild.Wrapper.Engine;

namespace SigilBuild.Installer.Host;

/// <summary>
/// Process-wide hand-off between <see cref="Program"/> (which parses argv and
/// builds the shared <see cref="InstallSession"/>) and <see cref="App"/> (which
/// wires the session's engine runner into the wizard's Installing screen).
/// A static holder is acceptable here: the host is a single-run, single-install
/// process, and <see cref="Avalonia.AppBuilder"/> constructs <see cref="App"/>
/// with no constructor arguments.
/// </summary>
internal static class HostRuntime
{
    /// <summary>The install session for the current interactive (wizard) run, or null in headless mode.</summary>
    public static InstallSession? Session { get; set; }

    /// <summary>
    /// This process's original argument vector, kept so the wizard's machine-scope
    /// handoff can rebuild it for the elevated child rather than inventing a command
    /// line from nothing. Empty for a plain double-click, which is the common case.
    /// </summary>
    /// <remarks>
    /// Needed because scope cannot be changed inside a running session (R90): picking
    /// "All users of this computer" starts a new elevated run, and that run has to
    /// inherit everything the user originally passed — <c>/LOG=</c>, <c>/lang=</c>, a
    /// parameter value — with only the scope flag replaced.
    /// </remarks>
    public static System.Collections.Generic.IReadOnlyList<string> Args { get; set; }
        = System.Array.Empty<string>();
}
