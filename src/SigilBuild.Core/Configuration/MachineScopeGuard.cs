using System.Collections.Generic;
using SigilBuild.Core.Diagnostics;
using SigilBuild.Core.Manifest;

namespace SigilBuild.Core.Configuration;

/// <summary>
/// P11: shared pack-time guard for install steps that touch machine-global state
/// (<see cref="InstallStep.RequiresMachineScope"/>). T11.1-T11.3 add the first
/// three such steps (<c>scheduled_task_create</c>, <c>com_register</c>,
/// <c>firewall_rule</c>) and each overrides the flag to <c>true</c>; THIS type
/// carries no knowledge of those step types — it only reacts to the flag, so it
/// needs no changes when they land. A manifest that declares any such step while
/// the installer might still run in per-user scope is refused at pack time with
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
/// <see cref="ValidateStep"/> once per step, immediately after the step record
/// is constructed, passing that call site's own precise <see cref="SourceLocation"/>
/// — the same one used for the sibling SIG0230/SIG0231/SIG0232 diagnostics on
/// that node — never the manifest-root location. That is what lets a SIG0310 on
/// step #40 of a 40-step <c>install_steps:</c> list point at step #40's own YAML
/// node instead of line 1. The resolved scope is threaded down alongside it: it
/// is known before any step collection is parsed (<see cref="InstallerSection.Scope"/>
/// is resolved before <c>installer.hooks</c> is parsed, and the whole
/// <c>installer:</c> block is resolved before the root-level step collections
/// are parsed), so every call site already has the real scope in hand — there
/// is no need for a separate whole-manifest pass after the fact.
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
