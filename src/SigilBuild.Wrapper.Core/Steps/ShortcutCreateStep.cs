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

        var locationDir = ResolveLocation(_spec.Location);
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
    /// directory. The two named anchors map onto the calling user's
    /// per-profile shell folders; anything else is treated as an explicit
    /// path so manifests can target arbitrary install dirs (e.g. a
    /// per-machine "All Users" Start Menu via <c>${parameters.allusers}</c>).
    /// </summary>
    private static string ResolveLocation(string location) => location switch
    {
        "start_menu" => Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
        "desktop"    => Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        _            => location,
    };
}
