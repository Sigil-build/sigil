using System.Collections.Generic;

namespace SigilBuild.Core.Manifest;

/// <summary>
/// Lifecycle hooks (P2, gap G2) parsed from <c>installer.hooks</c>. Each phase is
/// an ordered list of ordinary step records (typically <c>run_program</c>) that
/// run <em>outside</em> the rollback journal, around the transactional install /
/// uninstall body.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hooks have NO rollback obligations.</b> Unlike the journaled
/// <c>install_steps</c> / top-level <c>pre_install</c> / <c>post_install</c>, a
/// hook's side effects are never recorded and never undone. A failing hook is
/// governed only by its own <c>on_failure</c>:
/// <list type="bullet">
///   <item><description><c>fail</c> — abort the operation. This is the default for
///   <see cref="PreInstall"/> and <see cref="PreUninstall"/>: a failed pre-hook
///   stops the run before the journal opens / before the uninstall replays.</description></item>
///   <item><description><c>continue</c> — log the failure and proceed. This is the
///   default for <see cref="PostInstall"/> and <see cref="PostUninstall"/>: the
///   install is already committed, so a failed post-hook cannot roll it back.</description></item>
/// </list>
/// </para>
/// <para>
/// Ordering: <see cref="PreInstall"/> runs before the journal opens;
/// <see cref="PostInstall"/> runs after the journal commits and before the Done
/// screen. <see cref="PreUninstall"/> / <see cref="PostUninstall"/> bracket the
/// uninstall journal replay symmetrically. Hook args may use <c>{var.*}</c> /
/// <c>{install_dir}</c> tokens.
/// </para>
/// </remarks>
public sealed record InstallerHooks(
    IReadOnlyList<InstallStep>? PreInstall = null,
    IReadOnlyList<InstallStep>? PostInstall = null,
    IReadOnlyList<InstallStep>? PreUninstall = null,
    IReadOnlyList<InstallStep>? PostUninstall = null);

/// <summary>
/// The <c>installer.run_after_install</c> target (P2, gap G4): the program the
/// Done screen's checked-by-default "Launch &lt;App&gt;" checkbox starts, and the
/// program a headless <c>/silent /launch</c> run starts. Always launched
/// <em>unelevated</em> (de-elevated when the installer itself ran as admin).
/// </summary>
/// <param name="Path">The program to launch. May use <c>{install_dir}</c> /
/// <c>{var.*}</c> / <c>payload://</c> tokens, resolved at run time.</param>
/// <param name="Args">Optional arguments; each may use the same tokens.</param>
public sealed record RunAfterInstall(string Path, IReadOnlyList<string>? Args = null);
