namespace SigilBuild.Wrapper.Engine;

using System;
using System.Globalization;
using System.Runtime.Versioning;

/// <summary>
/// The anchor every SYSTEM-level step target must clear (register rows R3 and
/// R9): <c>scheduled_task_create.program</c> (<c>/RU SYSTEM</c>),
/// <c>service_install.binary_path</c>, <c>com_register.path</c> (loaded into the
/// elevated installer process) and <c>firewall_rule.program</c>.
/// </summary>
/// <remarks>
/// <para>
/// Two independent checks, both required:
/// </para>
/// <list type="number">
///   <item><description>
///     <see cref="PathContainment.IsUnderWithoutTraversal"/> against
///     <c>ctx.InstallDir</c> — the target really lives inside the install
///     directory, textually and physically (no junction on the way down).
///   </description></item>
///   <item><description>
///     <see cref="StateDirectorySecurity.IsAdminOnlyWritable"/> on the target —
///     only SYSTEM, <c>BUILTIN\Administrators</c> or TrustedInstaller can write
///     the directory the target sits in.
///   </description></item>
/// </list>
/// <para>
/// The second check is the one that actually stops the attack. Containment alone
/// is satisfied by any path under <c>install_dir</c>, and an <c>install_dir</c>
/// can itself be user-writable — a per-user install root always is. R3's payload
/// is "a SYSTEM-scheduled task pointing at a binary any user can replace", and
/// only the ACL predicate answers that question.
/// </para>
/// <para>
/// <b>Both checks apply in every scope, not only machine scope.</b> Three of the
/// four steps are machine-scope-only by construction
/// (<see cref="SigilBuild.Core.Manifest.InstallStep.RequiresMachineScope"/>);
/// <c>service_install</c> is not, but <c>sc create</c> needs administrator rights
/// regardless, and a service whose binary sits in <c>%LocalAppData%</c> is
/// precisely R3 — the user who can replace that binary gets LocalSystem code
/// execution. Gating the ACL check on machine scope would leave that open.
/// </para>
/// <para>
/// A run with no resolved <c>install_dir</c> is refused rather than waved
/// through: there is no anchor to check against, so no target can be shown safe.
/// Production always has one — <see cref="InstallDirResolver.Resolve(SigilBuild.Core.Manifest.InstallScope, string?, string, string?, string?, string?, string?)"/>
/// never returns null and <see cref="StepContext.From"/> always calls it — so
/// this branch is reachable only from a hand-built context (the step unit tests).
/// </para>
/// <para>
/// <b><c>payload://</c> targets are refused, and that is a decision rather than a
/// side effect.</b> Register row R9 proposes "resolve privileged step targets only
/// from <c>payload://</c> or a contained <c>{install_dir}</c>", i.e. it treats the
/// extracted payload as a safe source. It is not one, for two independent reasons:
/// </para>
/// <list type="number">
///   <item><description>
///     <see cref="PayloadExtraction"/> extracts to
///     <c>%TEMP%\sigil-&lt;appid&gt;-&lt;random&gt;</c>. Under an elevated install that
///     is the invoking user's own temp directory, which that user can write — so a
///     payload-rooted service binary or COM DLL is replaceable between extraction
///     and use. That is R3's attack with a different directory, and
///     <see cref="StateDirectorySecurity.IsAdminOnlyWritable"/> correctly answers
///     false for it.
///   </description></item>
///   <item><description>
///     <see cref="InstallSession"/> owns that directory's lifetime and deletes it
///     when the run ends. A scheduled task, service or COM registration pointing
///     into it would be left pointing at a path that no longer exists — so a
///     payload-rooted privileged target is broken on its own terms even with the
///     security question set aside.
///   </description></item>
/// </list>
/// <para>
/// The supported shape is the one the guide already prescribes for
/// <c>service_install</c>: <c>file_copy</c> the binary into <c>install_dir</c>
/// first, then point the privileged step at <c>{install_dir}\…</c>. A
/// <c>payload://</c> target is reported by the containment arm below, whose
/// message names <c>install_dir</c>; <c>docs/guides/install-steps.md</c> states
/// the rule and the workaround.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class PrivilegedTargetGuard
{
    /// <summary>
    /// Returns <c>null</c> when <paramref name="resolved"/> is an acceptable
    /// privileged target, or a step-failure message naming the check that
    /// refused it.
    /// </summary>
    /// <param name="stepType">Manifest step type, e.g. <c>scheduled_task_create</c>.</param>
    /// <param name="field">Manifest field carrying the target, e.g. <c>program</c>.</param>
    /// <param name="installDir">The run's resolved <c>install_dir</c> (<c>ctx.InstallDir</c>).</param>
    /// <param name="resolved">The already token-expanded target path.</param>
    public static string? Check(string stepType, string field, string? installDir, string resolved)
    {
        if (string.IsNullOrWhiteSpace(installDir))
        {
            return Refuse(
                stepType,
                field,
                resolved,
                "this run has no resolved install_dir, so the target cannot be anchored");
        }

        if (!PathContainment.IsUnderWithoutTraversal(installDir, resolved))
        {
            return Refuse(
                stepType,
                field,
                resolved,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"it does not resolve inside install_dir ('{installDir}'), or reaches it " +
                    $"through a directory junction"));
        }

        if (!StateDirectorySecurity.IsAdminOnlyWritable(resolved))
        {
            return Refuse(
                stepType,
                field,
                resolved,
                "its directory is writable by a non-administrator, so an unprivileged user " +
                "could replace the target after the install completes");
        }

        return null;
    }

    private static string Refuse(string stepType, string field, string resolved, string why) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{stepType}: refusing the privileged '{field}' target '{resolved}' — {why}. " +
            $"This step runs with SYSTEM-level authority; see the containment note in " +
            $"docs/guides/install-steps.md.");
}
