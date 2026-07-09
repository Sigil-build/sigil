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
}
