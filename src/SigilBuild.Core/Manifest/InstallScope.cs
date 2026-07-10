namespace SigilBuild.Core.Manifest;

/// <summary>
/// Target scope for an installer. Mirrors the schema's <c>installer.scope</c>
/// enum exactly: keep these two surfaces in sync.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><description><see cref="User"/> — per-user install
///     (<c>%LocalAppData%\Programs</c>, HKCU ARP, user PATH, no elevation).</description></item>
///   <item><description><see cref="Machine"/> — per-machine install
///     (Program Files, HKLM ARP, machine PATH, elevation required).</description></item>
///   <item><description><see cref="Auto"/> — user scope unless overridden by
///     <c>/allusers</c> or the wizard's scope toggle. Default.</description></item>
/// </list>
/// Scope resolution and elevation behaviour land with T12; this enum is only
/// the manifest/blob data surface.
/// </remarks>
public enum InstallScope
{
    User,
    Machine,
    Auto,
}
