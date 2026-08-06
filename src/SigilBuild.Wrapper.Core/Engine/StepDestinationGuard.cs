namespace SigilBuild.Wrapper.Engine;

using System;
using System.Globalization;

/// <summary>
/// Register row R16: every step destination is contained to <c>install_dir</c>.
/// </summary>
/// <remarks>
/// <para>
/// Before this, no step destination was checked at all.
/// <c>File.WriteAllText</c> traverses reparse points and truncates an existing
/// target in place, keeping its prior DACL — so an attacker-planted placeholder
/// stays attacker-writable after the elevated installer writes to it — and
/// <c>Directory.CreateDirectory</c> would happily materialize a whole tree
/// outside the install directory.
/// </para>
/// <para>
/// The OTHER half of R16 — an unresolved <c>{token}</c> in a path — is not here.
/// It lives in <see cref="StepContext.ResolvePath"/> via
/// <see cref="BraceTokenScanner"/>, because it applies to every path-valued step
/// field without exception and has no opt-out, while containment is per-step and
/// does have one.
/// </para>
/// </remarks>
internal static class StepDestinationGuard
{
    /// <summary>
    /// Returns <c>null</c> when <paramref name="resolved"/> is an acceptable
    /// destination for this run, or a step-failure message explaining the
    /// refusal.
    /// </summary>
    /// <param name="installDir">The run's resolved <c>install_dir</c> (<c>ctx.InstallDir</c>).</param>
    /// <param name="stepType">Manifest step type, e.g. <c>file_copy</c>.</param>
    /// <param name="field">Manifest field carrying the destination, e.g. <c>to</c>.</param>
    /// <param name="resolved">The destination AFTER the step's own token expansion.</param>
    /// <param name="allowOutsideInstallDir">The step's <c>allow_outside_install_dir</c> opt-out.</param>
    public static string? Check(
        string? installDir, string stepType, string field, string resolved, bool allowOutsideInstallDir)
    {
        if (allowOutsideInstallDir)
        {
            return null;
        }

        // No anchor to check against. Production always has one: StepContext.From
        // calls InstallDirResolver.Resolve, which never returns null, so this
        // branch is reachable only from a hand-built context (the step unit
        // tests). StepDestinationGuardTests pins that claim.
        if (string.IsNullOrWhiteSpace(installDir))
        {
            return null;
        }

        if (!PathContainment.IsUnderWithoutTraversal(installDir, resolved))
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{stepType}: the '{field}' path '{resolved}' is outside install_dir " +
                $"('{installDir}'), or reaches it through a directory junction. Set " +
                $"'allow_outside_install_dir: true' on this step if the write really is " +
                $"meant to land outside the installed application.");
        }

        return null;
    }
}
