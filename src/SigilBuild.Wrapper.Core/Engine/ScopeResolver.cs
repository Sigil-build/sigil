namespace SigilBuild.Wrapper.Engine;

using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Cli;

/// <summary>
/// Resolves the <em>effective</em> install scope (T12, decision 9) from the
/// manifest-declared scope and the command-line <c>/allusers</c> /
/// <c>/currentuser</c> override. The result is always a concrete
/// <see cref="InstallScope.User"/> or <see cref="InstallScope.Machine"/> — never
/// <see cref="InstallScope.Auto"/>, which is a manifest default, not a runtime
/// target.
/// </summary>
/// <remarks>
/// Decision table (manifest scope × flag → resolved scope, or exit 64):
/// <code>
/// manifest | /(none)  | /allusers      | /currentuser
/// ---------+----------+----------------+---------------
/// user     | user     | EXIT 64 (conflict) | user
/// machine  | machine  | machine        | EXIT 64 (conflict)
/// auto     | user     | machine        | user
/// </code>
/// A fixed <c>user</c> or <c>machine</c> manifest scope cannot be flipped to the
/// opposite scope: requesting it is a usage error (<see cref="UsageException"/>,
/// which the entry points translate to exit code 64). A flag that <em>agrees</em>
/// with the fixed scope is accepted as a harmless no-op. <c>auto</c> defaults to
/// user and is freely overridable (the wizard's scope toggle, T13, sets the same
/// override the flags do).
/// </remarks>
public static class ScopeResolver
{
    /// <summary>
    /// Resolve <paramref name="manifestScope"/> against the CLI
    /// <paramref name="flag"/> to a concrete <see cref="InstallScope.User"/> /
    /// <see cref="InstallScope.Machine"/>.
    /// </summary>
    /// <exception cref="UsageException">
    /// A flag conflicts with a fixed manifest scope (e.g. <c>/allusers</c> against
    /// a manifest <c>scope: user</c>). The message names the conflict.
    /// </exception>
    public static InstallScope Resolve(InstallScope manifestScope, ScopeOverride flag)
    {
        switch (manifestScope)
        {
            case InstallScope.User:
                if (flag == ScopeOverride.AllUsers)
                {
                    throw new UsageException(
                        "/allusers conflicts with the manifest's fixed 'user' install scope: this installer is per-user only");
                }
                return InstallScope.User;

            case InstallScope.Machine:
                if (flag == ScopeOverride.CurrentUser)
                {
                    throw new UsageException(
                        "/currentuser conflicts with the manifest's fixed 'machine' install scope: this installer is per-machine only");
                }
                return InstallScope.Machine;

            case InstallScope.Auto:
            default:
                return flag switch
                {
                    ScopeOverride.AllUsers => InstallScope.Machine,
                    // auto defaults to per-user; /currentuser is the explicit form
                    // of that default. The wizard scope toggle (T13) sets the same
                    // override.
                    _ => InstallScope.User,
                };
        }
    }
}
