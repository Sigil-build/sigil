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
}
