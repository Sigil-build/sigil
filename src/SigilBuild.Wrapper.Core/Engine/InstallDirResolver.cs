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
    public static string Resolve(
        InstallScope scope,
        string? appName,
        string appId,
        string? manifestInstallDir,
        string? cliOverride,
        string? collected = null)
    {
        var scopeRoot = ScopeLayout.For(scope).InstallRoot;
        var template = FirstNonBlank(collected, cliOverride, manifestInstallDir) ?? DefaultTemplate;
        var resolved = SubstituteDirTokens(template, scopeRoot, appName, appId);
        return Canonicalize(resolved);
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
