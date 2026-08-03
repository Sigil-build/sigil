namespace SigilBuild.Wrapper.Engine;

/// <summary>
/// Thrown by <see cref="InstallDirResolver"/> when the resolved install
/// directory falls outside the scope root (register row R3).
/// </summary>
/// <remarks>
/// <para>
/// <c>/D=</c> previously set <c>install_dir</c> to any path with no containment
/// check, and <c>{install_dir}</c> substitutes into
/// <c>scheduled_task_create.program</c> and <c>service_install.binary_path</c> —
/// both of which run as SYSTEM. <c>Setup.exe /allusers /D=C:\Users\Public\evil</c>
/// therefore created a SYSTEM-level task pointing at a directory any user can
/// write. This is a refusal, not an engine bug: nothing has been installed when
/// it is raised, so both the wizard and the silent path render it as a plain
/// install failure.
/// </para>
/// </remarks>
public sealed class InstallDirRejectedException : System.Exception
{
    public InstallDirRejectedException(string message) : base(message) { }

    public InstallDirRejectedException(string message, System.Exception inner) : base(message, inner) { }
}
