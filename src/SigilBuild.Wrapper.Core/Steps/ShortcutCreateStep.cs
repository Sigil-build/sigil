namespace SigilBuild.Wrapper.Steps;

using System;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Steps.Win32;

/// <summary>
/// MUST-tier <c>shortcut_create</c> step. Materialises a single Windows
/// <c>.lnk</c> file under the resolved location, then records a
/// <see cref="RollbackRecord.DeleteShortcut"/> so a failed install undoes the
/// shortcut. Named locations <c>start_menu</c> and <c>desktop</c> resolve
/// via <see cref="Environment.SpecialFolder"/>; any other value is treated
/// as an absolute filesystem path and created on demand.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ShortcutCreateStep : IStep
{
    private readonly InstallStep.ShortcutCreate _spec;

    public ShortcutCreateStep(InstallStep.ShortcutCreate spec)
    {
        _spec = spec;
    }

    public Task<StepResult> RunAsync(StepContext ctx, RollbackJournal journal, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(StepResult.Failed("shortcut_create requires Windows"));
        }

        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(journal);

        // EVERY substitution happens before the journal entry and before anything
        // touches the filesystem. ResolvePath throws on a path that still carries
        // an unresolved {token} (R16), so resolving after journaling would queue an
        // unconditional DeleteShortcut — see RollbackRecord.DeleteShortcut.UndoAsync
        // — for a .lnk this installer never created. Under `on_failure: continue`
        // (or any later rollback) that would delete a same-named PRE-EXISTING
        // shortcut on the all-users Desktop or Start Menu. Same ordering rule, and
        // the same reason, as ScheduledTaskCreateStep's R31 check.
        var argString = _spec.Args is null ? null : string.Join(" ", _spec.Args);

        var locationDir = ResolveLocation(_spec.Location, ctx);
        var name = ctx.Resolve(_spec.Name);
        var target = ctx.ResolvePath(_spec.Target);
        var arguments = argString is null ? null : ctx.Resolve(argString);
        var workingDirectory = _spec.WorkingDir is null ? null : ctx.ResolvePath(_spec.WorkingDir);
        var iconLocation = _spec.Icon is null ? null : ctx.ResolvePath(_spec.Icon);

        var containmentRefusal = CheckLocationContained(ctx, locationDir);
        if (containmentRefusal is not null)
        {
            return Task.FromResult(StepResult.Failed(containmentRefusal));
        }

        var lnkPath = Path.Combine(locationDir, name + ".lnk");

        // `name` goes through Resolve, not ResolvePath — it is a display name, not
        // a path — but it is concatenated into the file this step creates, so the
        // COMPOSED path is what has to be clean. Without this, a typo'd token in
        // `name` would still land a file literally called "{var.typo}.lnk".
        var token = BraceTokenScanner.FirstUnresolved(lnkPath);
        if (token is not null)
        {
            return Task.FromResult(StepResult.Failed(
                $"shortcut_create: the shortcut path '{lnkPath}' still contains the unresolved " +
                $"token '{{{token}}}' after substitution. Refusing to create it — check the " +
                $"spelling of 'name' and 'location'."));
        }

        Directory.CreateDirectory(locationDir);

        // Record rollback BEFORE creating the .lnk — a crash mid-Save still
        // leaves the journal able to scrub the half-written file. Everything
        // above this line either returns without touching anything or is
        // side-effect-free.
        journal.Append(new RollbackRecord.DeleteShortcut(lnkPath));

        ShellLink.Save(
            lnkPath: lnkPath,
            target: target,
            arguments: arguments,
            workingDirectory: workingDirectory,
            iconLocation: iconLocation,
            description: _spec.Description);

        return Task.FromResult(StepResult.Ok());
    }

    /// <summary>
    /// Resolve the manifest's <c>location:</c> string into a real filesystem
    /// directory. The two named anchors map onto the <em>scope-correct</em> shell
    /// folders (T12): a per-machine install writes to the all-users (common)
    /// Desktop / Start Menu, a per-user install to the calling user's per-profile
    /// folders. Anything else is treated as an explicit path so manifests can
    /// still target arbitrary install dirs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// R16: the explicit-path branch went through <b>no substitution at all</b> —
    /// not <c>Resolve</c>, not <c>ResolvePath</c> — and its result feeds
    /// <see cref="Directory.CreateDirectory(string)"/>. So
    /// <c>location: "{install_dir}\Tools"</c> created a directory literally named
    /// <c>{install_dir}</c> next to the running installer and reported success:
    /// register row R16's headline symptom, in the one path field a check on
    /// substituted output could never catch, because nothing was ever substituted.
    /// The shipped example manifest
    /// (<c>examples/exe-wrapper/hello-wix-killer/sigil.yaml</c>) exercises this
    /// field with <c>${parameters.install_dir}\StartMenu</c>, so it was reachable
    /// by following the documentation.
    /// </para>
    /// <para>
    /// It routes through <see cref="StepContext.ResolvePath"/> now, which brings
    /// the <c>${...}</c> / <c>{...}</c> expansion it should always have had, the
    /// unresolved-token refusal, and the <c>payload://</c> traversal guard.
    /// </para>
    /// <para>
    /// <b>R16's <c>install_dir</c> containment deliberately does not apply here.</b>
    /// The whole point of <c>desktop</c> and <c>start_menu</c> is to write outside
    /// <c>install_dir</c> — anchoring this field on the install directory would
    /// refuse every ordinary shortcut. <c>shortcut_create</c> is correspondingly
    /// not in R16's list of contained destinations and does not accept
    /// <c>allow_outside_install_dir</c>. A <em>wider</em> anchor does apply — see
    /// <see cref="CheckLocationContained"/> (register row R54).
    /// </para>
    /// </remarks>
    private static string ResolveLocation(string location, StepContext ctx) => location switch
    {
        "start_menu" => ctx.Layout.StartMenuFolder,
        "desktop" => ctx.Layout.DesktopFolder,
        _ => ctx.ResolvePath(location),
    };

    /// <summary>
    /// Register row R54: the named anchors are contained by construction, the
    /// explicit-path branch was contained by nothing at all. Returns <c>null</c>
    /// when <paramref name="locationDir"/> sits under a root a shortcut may
    /// legitimately be written to, or a step-failure message naming those roots.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What was unbounded.</b> An explicit <c>location</c> reached
    /// <see cref="Directory.CreateDirectory(string)"/> and
    /// <see cref="ShellLink.Save"/> from an elevated process with no check, and
    /// the <see cref="RollbackRecord.DeleteShortcut"/> queued for it deletes
    /// whatever file that path names at rollback or uninstall. So a manifest —
    /// or a <c>${parameters.…}</c> / <c>{var.…}</c> value sourced from a wizard
    /// field or a <c>registry_read</c> — could materialize a directory tree
    /// anywhere on the volume and arrange for an arbitrary path to be deleted
    /// later.
    /// </para>
    /// <para>
    /// <b>The roots, and why each is on the list.</b> A shortcut belongs either
    /// in the installed application's own tree or in shortcut real estate:
    /// <list type="bullet">
    ///   <item><description><c>install_dir</c> — a portable
    ///   <c>{install_dir}\Tools</c> shortcut folder is ordinary.</description></item>
    ///   <item><description><b>Both</b> scopes' Start Menu and Desktop folders.
    ///   Those four are precisely where the named anchors point, so a manifest
    ///   asking for a Start Menu <em>vendor subfolder</em> —
    ///   <c>…\Start Menu\Programs\Contoso</c>, the single most common reason to
    ///   spell the path out — keeps working. Both scopes rather than the run's
    ///   own: a machine install writing a shortcut into the installing
    ///   administrator's own profile is not an escalation, and a user-scope run
    ///   cannot write the common folders anyway.</description></item>
    /// </list>
    /// There is no opt-out. Adding one would be a schema change, and no
    /// legitimate destination is known that these roots exclude; a manifest that
    /// really needs one can place the file with <c>file_copy</c>, which has
    /// <c>allow_outside_install_dir</c>.
    /// </para>
    /// <para>
    /// <b>No anchor, no check</b>, exactly as <see cref="StepDestinationGuard"/>
    /// does it: a context with no <c>install_dir</c> is only reachable from a
    /// hand-built one in the step unit tests. Production always has one —
    /// <c>StepContext.From</c> calls <c>InstallDirResolver.Resolve</c>, which
    /// never returns null.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// <c>internal</c> is the test seam: the accepting cases are real Start Menu
    /// and Desktop folders, and CI runs elevated, so a test that proved them by
    /// running the step would write a <c>.lnk</c> into the runner's own profile.
    /// Calling the predicate touches nothing.
    /// </remarks>
    internal static string? CheckLocationContained(StepContext ctx, string locationDir)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        if (string.IsNullOrWhiteSpace(ctx.InstallDir))
        {
            return null;
        }

        var roots = PermittedLocationRoots(ctx);
        foreach (var root in roots)
        {
            if (!string.IsNullOrWhiteSpace(root)
                && PathContainment.IsUnderWithoutTraversal(root, locationDir))
            {
                return null;
            }
        }

        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"shortcut_create: the 'location' path '{locationDir}' is outside every " +
            $"directory a shortcut may be written to ('{string.Join("', '", roots)}'), or " +
            $"reaches one through a directory junction. Use 'start_menu', 'desktop', a " +
            $"subfolder of either, or a path inside install_dir.");
    }

    private static string[] PermittedLocationRoots(StepContext ctx)
    {
        var machine = ScopeLayout.For(InstallScope.Machine);
        var user = ScopeLayout.For(InstallScope.User);

        return new[]
        {
            ctx.InstallDir!,
            machine.StartMenuFolder,
            machine.DesktopFolder,
            user.StartMenuFolder,
            user.DesktopFolder,
        };
    }
}
