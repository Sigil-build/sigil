namespace SigilBuild.Wrapper.Engine;

using System;
using System.IO;
using SigilBuild.Core.Manifest;

/// <summary>
/// Pins the <c>{install_dir}</c> contract (T13): computes the <em>effective</em>
/// install directory for a run from the resolved scope, the app identity, the
/// manifest override, and the command-line / wizard overrides — and resolves the
/// <c>{scope_root}</c> / <c>{app.*}</c> tokens an <c>install_dir</c> template may
/// carry.
/// </summary>
/// <remarks>
/// Precedence (highest wins):
/// <list type="number">
///   <item><description>the wizard-collected destination path (GUI), then</description></item>
///   <item><description><c>/D=path</c> (silent + GUI prefill), then</description></item>
///   <item><description>the prior install directory during an upgrade (P3 — preserve
///   the existing location / user data), then</description></item>
///   <item><description><c>installer.install_dir</c> (manifest override), then</description></item>
///   <item><description>the default <c>&lt;scope root&gt;\&lt;App.Name&gt;</c>.</description></item>
/// </list>
/// The chosen template has its <c>{scope_root}</c> / <c>{app.name}</c> /
/// <c>{app.id}</c> tokens substituted (a wizard/CLI absolute path carries none),
/// then is canonicalized via <see cref="Path.GetFullPath(string)"/> so a real
/// step target lands under a concrete directory rather than a literal
/// <c>{install_dir}</c> folder. The <c>{install_dir}</c> token itself is NOT
/// substituted here — that is the resolved output, and <see cref="StepContext"/>
/// substitutes it into step paths / expressions.
/// </remarks>
public static class InstallDirResolver
{
    /// <summary>
    /// The default install-dir template when neither the manifest nor a
    /// <c>/D=</c> / wizard override supplies one: the scope root joined with the
    /// app name (decision 9 / T12 scope roots).
    /// </summary>
    internal const string DefaultTemplate = "{scope_root}\\{app.name}";

    /// <summary>
    /// Resolve the effective install directory for <paramref name="scope"/>.
    /// </summary>
    /// <param name="scope">The resolved install scope (drives <c>{scope_root}</c>).</param>
    /// <param name="appName">The manifest's <c>App.Name</c> (default base + <c>{app.name}</c>); falls back to <paramref name="appId"/> when null/blank.</param>
    /// <param name="appId">The manifest's <c>App.Id</c> (<c>{app.id}</c>).</param>
    /// <param name="manifestInstallDir">The <c>installer.install_dir</c> override, or null.</param>
    /// <param name="cliOverride">The parsed <c>/D=path</c> value, or null.</param>
    /// <param name="collected">The wizard-collected destination path, or null.</param>
    /// <param name="priorInstallDir">
    /// The prior version's install directory during an upgrade / forced downgrade (P3),
    /// or null. Wins over the manifest default and the scope-root default so an upgrade
    /// lands in the existing location (preserving user data), but loses to an explicit
    /// <c>/D=</c> or wizard-collected path. Absolute — carries no <c>{...}</c> tokens.
    /// </param>
    /// <exception cref="InstallDirRejectedException">
    /// The resolved directory falls outside the scope's containment root, or
    /// reaches it through a reparse point (register row R3).
    /// </exception>
    public static string Resolve(
        InstallScope scope,
        string? appName,
        string appId,
        string? manifestInstallDir,
        string? cliOverride,
        string? collected = null,
        string? priorInstallDir = null)
        => Resolve(
            scope, appName, appId, manifestInstallDir, cliOverride,
            allowAnyRoot: false, collected, priorInstallDir);

    /// <summary>
    /// <see cref="Resolve(InstallScope, string?, string, string?, string?, string?, string?)"/>
    /// with an explicit containment opt-out.
    /// </summary>
    /// <param name="allowAnyRoot">
    /// When <c>true</c>, skip the R3 scope-root containment check. This exists
    /// for test fixtures that legitimately resolve into an OS temp directory —
    /// it is <c>internal</c> precisely so no production path can reach it. Never
    /// pass <c>true</c> from <c>src/</c>.
    /// </param>
    internal static string Resolve(
        InstallScope scope,
        string? appName,
        string appId,
        string? manifestInstallDir,
        string? cliOverride,
        bool allowAnyRoot,
        string? collected = null,
        string? priorInstallDir = null)
    {
        var layout = ScopeLayout.For(scope);
        var template = FirstNonBlank(collected, cliOverride, priorInstallDir, manifestInstallDir) ?? DefaultTemplate;
        var resolved = Canonicalize(SubstituteDirTokens(template, layout.InstallRoot, appName, appId));

        if (!allowAnyRoot)
        {
            EnsureContained(layout, resolved);
        }

        return resolved;
    }

    /// <summary>
    /// True when <paramref name="resolved"/> is inside the directory every
    /// <c>install_dir</c> for <paramref name="layout"/> must stay within
    /// (register row R3).
    /// </summary>
    /// <remarks>
    /// Machine scope anchors on <c>%ProgramFiles%</c>: <c>{install_dir}</c> feeds
    /// <c>scheduled_task_create.program</c> and <c>service_install.binary_path</c>,
    /// which run as SYSTEM, so the destination must not be a directory an
    /// unprivileged user can write.
    /// <para>
    /// User scope crosses no privilege boundary, so the root is widened from
    /// <c>%LocalAppData%\Programs</c> to the whole user profile — a user writing
    /// inside their own profile is not an escalation. The check is kept because
    /// an unanchored user-scope install still lets a manifest write anywhere the
    /// user can. <c>%LocalAppData%</c> can be redirected off the profile (folder
    /// redirection), so the scope's own install root is accepted as well;
    /// otherwise the DEFAULT install would be refused on such machines.
    /// </para>
    /// </remarks>
    internal static bool IsContained(ScopeLayout layout, string resolved)
    {
        ArgumentNullException.ThrowIfNull(layout);

        if (PathContainment.IsUnderWithoutTraversal(layout.InstallRoot, resolved))
        {
            return true;
        }

        return !layout.IsMachine
            && PathContainment.IsUnderWithoutTraversal(UserContainmentRoot, resolved);
    }

    private static string UserContainmentRoot =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static void EnsureContained(ScopeLayout layout, string resolved)
    {
        if (IsContained(layout, resolved))
        {
            return;
        }

        var root = layout.IsMachine ? layout.InstallRoot : UserContainmentRoot;

        throw new InstallDirRejectedException(
            string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"The install directory '{resolved}' is outside the {layout.Name} scope root '{root}' " +
                $"(or reaches it through a directory junction). Refusing to install there — " +
                $"{{install_dir}} feeds SYSTEM-level step targets. Nothing was installed."));
    }

    /// <summary>
    /// Substitute the directory-defining tokens (<c>{scope_root}</c>,
    /// <c>{app.name}</c>, <c>{app.id}</c>) in an <c>install_dir</c> template. Does
    /// NOT touch <c>{install_dir}</c> (that is the output of this resolution).
    /// </summary>
    internal static string SubstituteDirTokens(string template, string scopeRoot, string? appName, string appId)
    {
        ArgumentNullException.ThrowIfNull(template);
        var name = string.IsNullOrWhiteSpace(appName) ? appId : appName!;
        return template
            .Replace("{scope_root}", scopeRoot, StringComparison.Ordinal)
            .Replace("{app.name}", name, StringComparison.Ordinal)
            .Replace("{app.id}", appId, StringComparison.Ordinal);
    }

    private static string? FirstNonBlank(params string?[] candidates)
    {
        foreach (var c in candidates)
        {
            if (!string.IsNullOrWhiteSpace(c))
            {
                return c;
            }
        }
        return null;
    }

    private static string Canonicalize(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
#pragma warning disable CA1031 // A malformed template must not crash resolution; the raw value surfaces the problem downstream.
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
#pragma warning restore CA1031
    }
}
