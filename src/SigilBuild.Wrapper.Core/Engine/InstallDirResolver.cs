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
    /// for test fixtures that legitimately resolve to an arbitrary absolute path
    /// (the precedence suite in <c>InstallDirResolverTests</c>) — <b>never</b>
    /// pass <c>true</c> from <c>src/</c>.
    /// <para>
    /// <b>What <c>internal</c> does and does not guarantee here.</b> It keeps
    /// this overload off the package's public API surface, so no consumer of
    /// <c>SigilBuild.Wrapper.Core</c> can reach it. It does <b>not</b> fence off
    /// production code inside this repo:
    /// <c>SigilBuild.Wrapper.Core.csproj</c> grants <c>InternalsVisibleTo</c> to
    /// <c>SigilBuild.Wrapper</c>, <c>SigilBuild.Installer.Host</c> and
    /// <c>SigilBuild.Packaging</c> alongside the test assemblies, so any of those
    /// three <i>could</i> call this. None does — every <c>src/</c> call site uses
    /// the public seven-parameter overload, which hard-codes
    /// <c>allowAnyRoot: false</c>. The guarantee is convention plus review, not
    /// the compiler; treat a new <c>src/</c> caller as a security regression.
    /// </para>
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
        var resolved = ResolveRaw(layout, appName, appId, manifestInstallDir, cliOverride, collected, priorInstallDir);

        if (!allowAnyRoot && !IsExistingInstallLocation(layout, appName, appId, priorInstallDir, resolved))
        {
            EnsureContained(layout, resolved);
        }

        return resolved;
    }

    /// <summary>
    /// Apply the precedence and token substitution without any containment check.
    /// Shared by <see cref="Resolve(InstallScope, string?, string, string?, string?, bool, string?, string?)"/>
    /// and <see cref="GrandfatheredPriorDir"/> so the destination they reason
    /// about is computed exactly once, the same way.
    /// </summary>
    private static string ResolveRaw(
        ScopeLayout layout,
        string? appName,
        string appId,
        string? manifestInstallDir,
        string? cliOverride,
        string? collected,
        string? priorInstallDir)
    {
        var template = FirstNonBlank(collected, cliOverride, priorInstallDir, manifestInstallDir) ?? DefaultTemplate;
        return Canonicalize(SubstituteDirTokens(template, layout.InstallRoot, appName, appId));
    }

    /// <summary>
    /// True when the resolved destination IS the directory the application is
    /// already installed in — the <b>grandfather clause</b> for R3.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An install that already lives outside the scope root predates the
    /// containment rule. Refusing it strands the user with an app that can be
    /// neither upgraded nor cleanly removed, which is worse than the hole it
    /// closes — so re-installing over the app's own existing location is allowed
    /// even when that location is out of root.
    /// </para>
    /// <para>
    /// <b>Why this keys on the destination and not on which source won.</b> The
    /// first cut asked "did <paramref name="priorInstallDir"/> win the
    /// precedence?", which made the exemption unreachable through the wizard:
    /// <c>App.axaml.cs</c> prefills the Destination screen with the prior
    /// directory, and the install runner writes that value straight back as
    /// <c>collected</c> on EVERY headed run. A prefill echoed back is not a user
    /// choice, so the source-based test saw a "chosen" path and refused the very
    /// upgrade the ruling exists to permit — silently, since the exemption never
    /// fired and so never logged. Keying on the destination preserves the real
    /// distinction ("this is where the app already is" versus "the user picked
    /// somewhere new") no matter which field carried the value.
    /// </para>
    /// <para>
    /// <b>Why it is not a blanket exemption.</b> It grants exactly one directory:
    /// the app's current location. Any other out-of-root path — typed into the
    /// wizard, passed as <c>/D=</c>, or declared in the manifest — resolves to
    /// something different and is refused as before. Re-installing where the app
    /// already is confers no capability an attacker does not already have: if a
    /// SYSTEM-level step target points into that directory, it does so today.
    /// </para>
    /// <para>
    /// <b>Provenance of <paramref name="priorInstallDir"/>.</b> It is read from
    /// the ARP registry (<c>InstalledStateResolver</c>,
    /// <c>HKLM|HKCU\...\Uninstall\&lt;appId&gt;\InstallLocation</c>) — not from
    /// lane S1's persisted state file. Machine scope is protected by the HKLM
    /// ACL together with <c>InstallSession</c>'s <c>FoundScope == _scope</c>
    /// guard, so a user-writable HKCU value can never satisfy a machine-scope
    /// run. User scope has no such gate but crosses no privilege boundary: a
    /// user rewriting their own HKCU value gains nothing they could not already
    /// do directly.
    /// </para>
    /// </remarks>
    private static bool IsExistingInstallLocation(
        ScopeLayout layout, string? appName, string appId, string? priorInstallDir, string resolved)
    {
        if (string.IsNullOrWhiteSpace(priorInstallDir))
        {
            return false;
        }

        var prior = Canonicalize(SubstituteDirTokens(priorInstallDir!, layout.InstallRoot, appName, appId));
        return string.Equals(
            Path.TrimEndingDirectorySeparator(prior),
            Path.TrimEndingDirectorySeparator(resolved),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The resolved destination when it is the app's existing (grandfathered)
    /// install location AND falls outside the containment root — the case
    /// callers must log. <c>null</c> when there is nothing to report.
    /// </summary>
    /// <remarks>
    /// Exposed so <c>InstallSession</c> (which owns the <c>/LOG</c> sink) can
    /// record the exemption. A quiet allowance is how an exemption becomes the
    /// norm, so this path is never silent — and because it keys on the same
    /// destination test the resolver uses, it fires on the headed path too.
    /// </remarks>
    internal static string? GrandfatheredPriorDir(
        InstallScope scope,
        string? appName,
        string appId,
        string? manifestInstallDir,
        string? collected,
        string? cliOverride,
        string? priorInstallDir)
    {
        var layout = ScopeLayout.For(scope);
        var resolved = ResolveRaw(layout, appName, appId, manifestInstallDir, cliOverride, collected, priorInstallDir);

        if (!IsExistingInstallLocation(layout, appName, appId, priorInstallDir, resolved))
        {
            return null;
        }

        return IsContained(layout, resolved) ? null : resolved;
    }

    /// <summary>
    /// The scope's built-in default destination
    /// (<c>&lt;scope root&gt;\&lt;App.Name&gt;</c>), resolved WITHOUT the R3
    /// containment check and therefore guaranteed not to throw.
    /// </summary>
    /// <remarks>
    /// This exists for exactly one caller — <c>InstallSession.ResolveDefaultInstallDir</c>'s
    /// fallback, which runs inside a <c>catch (InstallDirRejectedException)</c>
    /// during Avalonia startup, before any window exists. Re-entering the
    /// throwing overload from that catch could raise a SECOND rejection
    /// (<c>&lt;InstallRoot&gt;\&lt;AppName&gt;</c> is itself junction-able), and
    /// that exception would escape the very catch written to prevent it, killing
    /// the wizard with no UI at all.
    /// <para>
    /// This is a DISPLAY value only and never reaches the engine: the
    /// destination the user confirms is re-resolved through the checking path in
    /// <c>InstallSession.RunInstallCoreAsync</c> and refused there. It is a
    /// separate member rather than an <c>allowAnyRoot: true</c> call precisely so
    /// that no <c>src/</c> code passes that flag.
    /// </para>
    /// </remarks>
    internal static string ScopeDefault(InstallScope scope, string? appName, string appId)
        => Canonicalize(
            SubstituteDirTokens(DefaultTemplate, ScopeLayout.For(scope).InstallRoot, appName, appId));

    /// <summary>
    /// True when <paramref name="resolved"/> is inside the directory every
    /// <c>install_dir</c> for <paramref name="layout"/> must stay within
    /// (register row R3).
    /// </summary>
    /// <remarks>
    /// The accepted roots are <see cref="ScopeLayout.InstallRoots"/> and nothing
    /// else — see that member for why each root is on the list. Deriving them
    /// there rather than restating them here is register row R52: before it, the
    /// <em>permitted</em> destinations lived in this method and the
    /// <em>default</em> destination lived in <c>ScopeLayout.InstallRoot</c>, and
    /// the two could drift apart silently (they already had: this method accepted
    /// <c>%ProgramFiles(x86)%</c>, which <c>ScopeLayout</c> did not model at all).
    /// </remarks>
    internal static bool IsContained(ScopeLayout layout, string resolved)
    {
        ArgumentNullException.ThrowIfNull(layout);

        foreach (var root in ContainmentRoots(layout))
        {
            if (PathContainment.IsUnderWithoutTraversal(root, resolved))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Every root a resolved <c>install_dir</c> for <paramref name="layout"/>
    /// may legitimately sit under — the layout's own declared root set (R52).
    /// A blank entry (e.g. <c>%ProgramFiles(x86)%</c> on a 32-bit-only OS) is
    /// harmless: <see cref="PathContainment.IsUnder"/> rejects a blank root.
    /// </summary>
    private static System.Collections.Generic.IReadOnlyList<string> ContainmentRoots(ScopeLayout layout) =>
        layout.InstallRoots;

    private static void EnsureContained(ScopeLayout layout, string resolved)
    {
        if (IsContained(layout, resolved))
        {
            return;
        }

        var roots = string.Join("', '", DistinctNonBlank(ContainmentRoots(layout)));

        throw new InstallDirRejectedException(
            string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"The install directory '{resolved}' is outside the {layout.Name} scope root '{roots}' " +
                $"(or reaches it through a directory junction). Refusing to install there — " +
                $"{{install_dir}} feeds SYSTEM-level step targets. Nothing was installed."));
    }

    private static System.Collections.Generic.List<string> DistinctNonBlank(
        System.Collections.Generic.IReadOnlyList<string> roots)
    {
        var seen = new System.Collections.Generic.List<string>(roots.Count);
        foreach (var r in roots)
        {
            if (string.IsNullOrWhiteSpace(r))
            {
                continue;
            }

            var duplicate = false;
            foreach (var already in seen)
            {
                if (string.Equals(already, r, StringComparison.OrdinalIgnoreCase))
                {
                    duplicate = true;
                    break;
                }
            }

            if (!duplicate)
            {
                seen.Add(r);
            }
        }
        return seen;
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
