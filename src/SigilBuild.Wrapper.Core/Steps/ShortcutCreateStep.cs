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

        var locationDir = ResolveLocation(_spec.Location, ctx);
        Directory.CreateDirectory(locationDir);
        var lnkPath = Path.Combine(locationDir, _spec.Name + ".lnk");

        // Record rollback BEFORE creating the .lnk — a crash mid-Save still
        // leaves the journal able to scrub the half-written file.
        journal.Append(new RollbackRecord.DeleteShortcut(lnkPath));

        var argString = _spec.Args is null
            ? null
            : string.Join(" ", _spec.Args);

        ShellLink.Save(
            lnkPath: lnkPath,
            // Target / working dir / icon may reference the extracted payload.
            target: ctx.ResolvePath(_spec.Target),
            arguments: argString is null ? null : ctx.Resolve(argString),
            workingDirectory: _spec.WorkingDir is null ? null : ctx.ResolvePath(_spec.WorkingDir),
            iconLocation: _spec.Icon is null ? null : ctx.ResolvePath(_spec.Icon),
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
    private static string ResolveLocation(string location, StepContext ctx) => location switch
    {
        "start_menu" => ctx.Layout.StartMenuFolder,
        "desktop"    => ctx.Layout.DesktopFolder,
        _            => location,
    };
}
