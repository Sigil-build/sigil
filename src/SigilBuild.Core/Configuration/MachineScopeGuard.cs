using System.Collections.Generic;
using SigilBuild.Core.Diagnostics;
using SigilBuild.Core.Manifest;

namespace SigilBuild.Core.Configuration;

/// <summary>
/// Shared pack-time guard for install steps that touch machine-global state
/// (<see cref="InstallStep.RequiresMachineScope"/>). THIS type carries no
/// knowledge of the individual step types — it only reacts to the flag, so a new
/// machine-scope-only step needs no change here. A manifest that declares any such
/// step while the installer might still run in per-user scope is refused at pack
/// time with
/// <see cref="DiagnosticCodes.SystemStepRequiresMachineScope"/> (SIG0310, Error).
/// </summary>
/// <remarks>
/// <para>
/// Only <see cref="InstallScope.Machine"/> guarantees the elevated, per-machine
/// install context these steps need. <see cref="InstallScope.User"/> obviously
/// doesn't; <see cref="InstallScope.Auto"/> ALSO fails the guard because it
/// resolves to per-user scope by default (overridden only at run time, via
/// <c>/allusers</c> or the wizard's scope toggle) — a manifest that ships a
/// machine-scope-only step must pin <c>scope: machine</c> explicitly rather than
/// lean on a runtime choice nobody has made yet.
/// </para>
/// <para>
/// Single-step signature by design: <c>ManifestParser.ParseInstallStep</c> calls
/// <see cref="ValidateStep"/> once per step, immediately after the step record is
/// constructed, passing that call site's own precise <see cref="SourceLocation"/> —
/// never the manifest-root location — so a SIG0310 on step #40 points at step #40's
/// own YAML node. The resolved scope is known before any step collection is parsed
/// (<see cref="InstallerSection.Scope"/> resolves before <c>installer.hooks</c>, and
/// the whole <c>installer:</c> block resolves before the root-level step
/// collections), so no separate whole-manifest pass is needed.
/// </para>
/// </remarks>
internal static class MachineScopeGuard
{
    /// <summary>
    /// Does THIS step under THIS scope emit SIG0310? Emits at most one
    /// diagnostic, at <paramref name="location"/> — the offending step's own
    /// node location, never a shared/root location. A no-op when
    /// <paramref name="scope"/> is <see cref="InstallScope.Machine"/> or
    /// <paramref name="step"/>'s <see cref="InstallStep.RequiresMachineScope"/>
    /// is false.
    /// </summary>
    internal static void ValidateStep(
        InstallStep step,
        InstallScope scope,
        SourceLocation location,
        List<Diagnostic> diagnostics)
    {
        if (scope == InstallScope.Machine || !step.RequiresMachineScope) return;

        diagnostics.Add(new Diagnostic(
            DiagnosticSeverity.Error,
            DiagnosticCodes.SystemStepRequiresMachineScope,
            $"step '{step.Id}' ({step.GetType().Name}) requires installer scope: machine, " +
            "but this manifest does not set installer.scope: machine",
            location,
            "https://docs.sigil.build/diagnostics/SIG0310"));
    }
}
