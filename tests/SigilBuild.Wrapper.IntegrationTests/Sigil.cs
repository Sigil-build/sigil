using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SigilBuild.Core.Configuration;
using SigilBuild.Core.Manifest;
using SigilBuild.Packaging;
using SigilBuild.Packaging.ExeWrapper;

namespace SigilBuild.Wrapper.IntegrationTests;

/// <summary>
/// Test-side helper that drives the EXE-wrapper packager end-to-end. Mirrors
/// what the <c>sigil pack</c> CLI does, but without the System.CommandLine
/// surface — the integration tests just need a function that turns a manifest
/// path into a setup exe on disk.
/// </summary>
internal static class Sigil
{
    /// <summary>
    /// Pack a manifest at the given path into the given output directory using
    /// the EXE-wrapper packager. Returns the absolute path to the produced exe.
    /// </summary>
    public static async Task<string> PackAsync(string manifestPath, string outputDir)
    {
        var loadResult = await ManifestLoader
            .LoadAsync(manifestPath, new ProcessEnvironmentReader())
            .ConfigureAwait(false);
        if (loadResult.Manifest is null)
        {
            throw new System.InvalidOperationException(
                "manifest validation failed: " +
                string.Join("; ", loadResult.Diagnostics.Select(d => d.Message)));
        }

        Directory.CreateDirectory(outputDir);

        var manifestDir = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        var sourceDir = Path.IsPathRooted(loadResult.Manifest.Build.Source)
            ? loadResult.Manifest.Build.Source
            : Path.Combine(manifestDir, loadResult.Manifest.Build.Source);

        var options = new PackOptions(
            SourceDirectory: sourceDir,
            OutputDirectory: outputDir,
            Format: PackageFormat.Exe,
            Architecture: TargetArchitecture.X64);

        var packager = new ExeWrapperPackager();
        var result = await packager
            .PackAsync(loadResult.Manifest, options, CancellationToken.None)
            .ConfigureAwait(false);
        if (result.Artifact is null)
        {
            throw new System.InvalidOperationException("pack produced no artifact");
        }
        return result.Artifact.Path;
    }

    /// <summary>
    /// Emit <paramref name="value"/> as a YAML <b>single-quoted</b> scalar — the one
    /// quoting style in which a Windows path is safe to interpolate.
    /// </summary>
    /// <remarks>
    /// A double-quoted YAML scalar treats <c>\</c> as an escape character, so a Windows
    /// path like <c>Software\SigilPrereqTest\…</c> fails to parse ("While scanning a
    /// quoted scalar, found unknown escape character") — every interpolated fixture
    /// scalar in this project goes through here rather than being hand-doubled or
    /// double-quoted (R66). In a single-quoted scalar the only escape is <c>''</c> for a
    /// literal apostrophe: backslashes need no treatment, and a value that would
    /// otherwise look like a YAML token (<c>{install_dir}</c>, <c>*</c>, <c>&amp;</c>, a
    /// leading digit) stays a plain string.
    /// </remarks>
    public static string YamlQuote(string value)
    {
        System.ArgumentNullException.ThrowIfNull(value);
        return "'" + value.Replace("'", "''", System.StringComparison.Ordinal) + "'";
    }

    /// <summary>
    /// Walk up from the test assembly's location to the directory holding
    /// <c>Sigil.slnx</c> — the repo root the on-disk fixtures and shipped examples are
    /// addressed from.
    /// </summary>
    public static string RepoRoot()
    {
        var dir = System.AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Sigil.slnx")))
            {
                return dir;
            }
            dir = Path.GetDirectoryName(dir);
        }
        throw new System.InvalidOperationException("could not locate Sigil.slnx");
    }

    /// <summary>
    /// Resolve a repo-relative path (forward slashes) against <see cref="RepoRoot"/>.
    /// </summary>
    public static string RepoPath(string relative)
    {
        System.ArgumentNullException.ThrowIfNull(relative);
        return Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
    }
}
