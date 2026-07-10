namespace SigilBuild.Wrapper.Engine;

using System;
using System.IO;
using SigilBuild.Core.Manifest;

/// <summary>
/// The per-scope filesystem / registry mapping for a resolved install scope
/// (T12). Given a concrete <see cref="InstallScope.User"/> or
/// <see cref="InstallScope.Machine"/> it exposes the install root, the state /
/// journal root, the ARP registry hive, the <c>env_set</c> PATH scope, and the
/// shortcut folders — so every scope-varying decision is parameterized in one
/// place rather than hardcoded at each call site.
/// </summary>
/// <remarks>
/// <list type="table">
///   <item><term>Install root</term><description><c>%ProgramFiles%</c> (machine) vs <c>%LocalAppData%\Programs</c> (user)</description></item>
///   <item><term>State / journal root</term><description><c>%ProgramData%</c> (machine) vs <c>%LocalAppData%</c> (user)</description></item>
///   <item><term>ARP hive</term><description>HKLM (machine) vs HKCU (user)</description></item>
///   <item><term>PATH scope</term><description>machine env vs user env</description></item>
///   <item><term>Shortcuts</term><description>common (all-users) vs per-user desktop / start menu</description></item>
/// </list>
/// </remarks>
public sealed class ScopeLayout
{
    private ScopeLayout(InstallScope scope)
    {
        Scope = scope;
    }

    /// <summary>The resolved scope — always <see cref="InstallScope.User"/> or <see cref="InstallScope.Machine"/>.</summary>
    public InstallScope Scope { get; }

    /// <summary>True for a per-machine install (Program Files, HKLM, machine PATH, elevation).</summary>
    public bool IsMachine => Scope == InstallScope.Machine;

    /// <summary>The lowercase scope name exposed to the expression engine (<c>"machine"</c> / <c>"user"</c>).</summary>
    public string Name => IsMachine ? "machine" : "user";

    /// <summary>
    /// The <c>env_set</c> registry scope string (<c>"machine"</c> / <c>"user"</c>)
    /// consumed by <see cref="Steps.EnvSetStep"/> when a step defers to the install
    /// scope (a manifest step scope of <c>auto</c>).
    /// </summary>
    public string EnvScope => Name;

    /// <summary>
    /// Build the layout for a resolved scope. <see cref="InstallScope.Auto"/> is
    /// treated as user (the auto default) so callers can pass a not-yet-resolved
    /// scope safely, though the resolver normally hands a concrete value.
    /// </summary>
    public static ScopeLayout For(InstallScope resolved) =>
        new(resolved == InstallScope.Machine ? InstallScope.Machine : InstallScope.User);

    /// <summary>
    /// The install root: <c>%ProgramFiles%</c> for machine scope,
    /// <c>%LocalAppData%\Programs</c> for user scope. Surfaced to the expression
    /// engine / templates as <c>scope.root</c> and used as the default install-dir
    /// base (T13).
    /// </summary>
    public string InstallRoot => IsMachine
        ? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
        : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs");

    /// <summary>
    /// The state / rollback-journal root: <c>%ProgramData%</c> for machine scope,
    /// <c>%LocalAppData%</c> for user scope. The per-app uninstall state lives
    /// under <c>&lt;StateRoot&gt;\Sigil\&lt;AppId&gt;</c>.
    /// </summary>
    public string StateRoot => IsMachine
        ? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
        : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>
    /// The per-user vs all-users Desktop directory for shortcut placement.
    /// </summary>
    public string DesktopFolder => IsMachine
        ? Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
        : Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

    /// <summary>
    /// The per-user vs all-users Start Menu directory for shortcut placement.
    /// </summary>
    public string StartMenuFolder => IsMachine
        ? Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
        : Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
}
