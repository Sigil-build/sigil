using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using SigilBuild.Core.Diagnostics;
using SigilBuild.Core.Manifest;
using SigilBuild.Packaging.Common;
using SigilBuild.Packaging.Installer;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Json;

namespace SigilBuild.Packaging.ExeWrapper;

/// <summary>
/// Packs an application as a single self-extracting <c>.exe</c> wrapper —
/// the AOT-published <c>SigilBuild.Wrapper</c> runtime, stamped with a step
/// blob and the payload archive as Win32 resources (see ADR-008).
/// </summary>
/// <remarks>
/// Task 14 implementation: the packager copies the stub runtime to
/// <c>{OutputDirectory}/{App.Name}-Setup.exe</c>, then embeds the JSON
/// step + parameter blob (<c>SIGIL_BLOB_V1</c>) and a zip archive of the
/// source directory (<c>SIGIL_PAYLOAD_V1</c>) as Win32 <c>RT_RCDATA</c>
/// resources via <see cref="WrapperResourceWriter"/>.
/// </remarks>
public sealed class ExeWrapperPackager : IPackager
{
    public PackageFormat Format => PackageFormat.Exe;

    public async Task<PackResult> PackAsync(SigilManifest manifest, PackOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(options);

        // Locate the AOT-published wrapper runtime. Surface the missing-runtime
        // case as a SIG0120 diagnostic — the dev workflow expects build-wrappers
        // to stage the AOT exe alongside the SDK before pack-time.
        string stubPath;
        try
        {
            stubPath = WrapperRuntimeLocator.Locate();
        }
        catch (FileNotFoundException ex)
        {
            return new PackResult(null, new[]
            {
                new Diagnostic(DiagnosticSeverity.Error, "SIG0120",
                    $"EXE-wrapper packaging requires the AOT-published SigilBuild.Wrapper runtime. {ex.Message}",
                    SourceLocation.Unknown,
                    "https://docs.sigil.build/diagnostics/SIG0120"),
            });
        }

        // Output filename mirrors the Zip/Msix convention: id-version-arch tag.
        // Sanitize the user-controlled App.Name segment against path-traversal /
        // illegal characters so a malicious or accidental manifest cannot escape
        // the output directory.
        var archStr = options.Architecture.ToString().ToLowerInvariant();
        var safeName = SanitizeFileNameSegment(manifest.App.Name);
        var outputName = $"{safeName}-{manifest.App.Version}-{archStr}-Setup.exe";
        var outputPath = Path.Combine(options.OutputDirectory, outputName);

        Directory.CreateDirectory(options.OutputDirectory);
        if (File.Exists(outputPath))
            File.Delete(outputPath);
        File.Copy(stubPath, outputPath);

        ct.ThrowIfCancellationRequested();

        // Build the SIGIL_BLOB_V1 wire payload: serialize a
        // SerializableWrapperBlob via the source-generated context shared
        // with the wrapper runtime.
        var blobBytes = BuildBlobBytes(manifest);

        // Build the SIGIL_PAYLOAD_V1 wire payload: a zip archive of the
        // source directory. Richer payload-extraction (zstd, splitting,
        // payload:// path resolution) lands with Tasks 15+.
        var payloadBytes = BuildPayloadBytes(options.SourceDirectory, ct);

        // Build the SIGIL_INSTALLER_HOST_V1 wire payload when the manifest
        // declares an `installer:` block AND the AOT-published installer.exe
        // is staged next to the SDK. When it's missing, fall through to the
        // headless path — the wrapper runs install_steps without a wizard.
        // This mirrors the policy MsixPackager.Bundle uses for MSIX.
        var (installerHostBundle, installerHostDiagnostic) =
            TryBuildInstallerHostBundle(manifest);

        // Embed all resources via the Win32 update-resource flow.
        await WrapperResourceWriter.WriteAsync(outputPath, blobBytes, payloadBytes, installerHostBundle, ct)
            .ConfigureAwait(false);

        // Compute sha256 + size *after* the resource embed so the artifact
        // descriptor reflects the final on-disk shape.
        var sha256 = ManifestHasher.Sha256(outputPath);
        var size = new FileInfo(outputPath).Length;

        var artifact = new PackedArtifact(outputPath, sha256, size);
        var diagnostics = installerHostDiagnostic is null
            ? Array.Empty<Diagnostic>()
            : new[] { installerHostDiagnostic };
        return new PackResult(artifact, diagnostics);
    }

    /// <summary>
    /// Try to build the <c>SIGIL_INSTALLER_HOST_V1</c> bundle. Returns
    /// <c>(null, null)</c> when the manifest has no <c>installer:</c> block;
    /// <c>(null, SIG0121-warning)</c> when the block is present but the
    /// AOT-published installer.exe is not staged (caller continues with a
    /// headless setup.exe); <c>(bundleBytes, null)</c> on success.
    /// </summary>
    private static (byte[]? Bundle, Diagnostic? Diagnostic) TryBuildInstallerHostBundle(SigilManifest manifest)
    {
        if (manifest.Installer is null)
        {
            return (null, null);
        }

        var hostExePath = InstallerHostLocator.TryLocate();
        if (hostExePath is null)
        {
            return (null, new Diagnostic(DiagnosticSeverity.Warning, "SIG0121",
                "manifest declares an installer: block but the AOT-published installer.exe was not found. " +
                "Setup.exe is built without a wizard — install_steps will run headlessly. " +
                "Stage installer.exe under runtimes/win-x64/ next to the SDK to enable the wizard.",
                SourceLocation.Unknown,
                "https://docs.sigil.build/diagnostics/SIG0121"));
        }

        // Resolve the brand logo path declared in the manifest (relative to
        // the manifest file's directory) and bundle the file under a
        // wizard-friendly name. When the manifest declares no logo (or the
        // file doesn't exist on the build machine), pass null and the wizard
        // falls back to the default no-logo state.
        string? brandLogoAbsolutePath = null;
        string? bundledLogoName = null;
        var brandLogo = manifest.Installer?.Brand?.Logo;
        if (!string.IsNullOrEmpty(brandLogo))
        {
            var manifestDir = Path.GetDirectoryName(manifest.Location.File);
            var candidate = Path.IsPathRooted(brandLogo) || string.IsNullOrEmpty(manifestDir)
                ? brandLogo
                : Path.GetFullPath(Path.Combine(manifestDir, brandLogo));
            if (File.Exists(candidate))
            {
                brandLogoAbsolutePath = candidate;
                bundledLogoName = InstallerHostBundle.BrandLogoEntryPrefix + Path.GetExtension(candidate);
            }
        }

        // BrandTokenEmitter is the shared brand-token serializer used by the
        // MSIX path too — see SigilBuild.Packaging.Installer.BrandTokenEmitter.
        // Warnings (e.g. WCAG-AA contrast failures) are accepted at pack time;
        // a future task can surface them through diagnostics.
        var tokens = BrandTokenEmitter.Emit(manifest, allowLowContrast: true, bundledLogoFileName: bundledLogoName);
        var installTimeParams = BrandTokenEmitter.EmitInstallTimeParameters(manifest);
        var bundle = InstallerHostBundle.Build(hostExePath, tokens, installTimeParams, brandLogoAbsolutePath);
        return (bundle, null);
    }

    private static byte[] BuildBlobBytes(SigilManifest manifest)
    {
        var parameters = manifest.Parameters is null
            ? Array.Empty<ParameterDefinition>()
            : ParametersToList(manifest.Parameters);

        var inMemory = new WrapperBlob(
            AppId: manifest.App.Id,
            App: new AppMetadata(
                Id: manifest.App.Id,
                Name: manifest.App.Name,
                Version: manifest.App.Version,
                Publisher: manifest.App.Publisher,
                Description: manifest.App.Description,
                Homepage: manifest.App.Homepage),
            Parameters: parameters,
            InstallSteps: manifest.InstallSteps ?? Array.Empty<InstallStep>(),
            PreInstall: manifest.PreInstall ?? Array.Empty<InstallStep>(),
            PostInstall: manifest.PostInstall ?? Array.Empty<InstallStep>(),
            // Update-step block doesn't yet exist on SigilManifest (Task 19+);
            // emit an empty list for forward compatibility.
            UpdateSteps: Array.Empty<InstallStep>(),
            PreUninstall: manifest.PreUninstall ?? Array.Empty<InstallStep>());

        var serializable = SerializableWrapperBlob.FromWrapperBlob(inMemory);
        var json = System.Text.Json.JsonSerializer.Serialize(
            serializable, WrapperBlobJsonContext.Default.SerializableWrapperBlob);
        return System.Text.Encoding.UTF8.GetBytes(json);
    }

    private static ParameterDefinition[] ParametersToList(
        IReadOnlyDictionary<string, ParameterDefinition> map)
    {
        if (map.Count == 0) return Array.Empty<ParameterDefinition>();
        var arr = new ParameterDefinition[map.Count];
        var i = 0;
        foreach (var kv in map)
        {
            arr[i++] = kv.Value;
        }
        return arr;
    }

    private static byte[] BuildPayloadBytes(string sourceDirectory, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(sourceDirectory) || !Directory.Exists(sourceDirectory))
        {
            return Array.Empty<byte>();
        }

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var root = Path.GetFullPath(sourceDirectory);
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                var entry = zip.CreateEntry(rel, CompressionLevel.Optimal);
                entry.LastWriteTime = DeterministicMtime;
                using var entryStream = entry.Open();
                using var fs = File.OpenRead(file);
                fs.CopyTo(entryStream);
            }
        }
        return ms.ToArray();
    }

    // 1980-01-01 00:00:00 UTC — earliest timestamp the ZIP format permits.
    // Fixed for deterministic byte-identical output across builds.
    private static readonly DateTimeOffset DeterministicMtime =
        new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static string SanitizeFileNameSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
            return "app";

        var invalid = Path.GetInvalidFileNameChars();
        Span<char> buffer = stackalloc char[segment.Length];
        var i = 0;
        foreach (var c in segment)
        {
            // Reject path separators outright (they're in InvalidFileNameChars on Windows
            // but listed defensively for cross-platform clarity).
            if (c is '/' or '\\' or ':' || Array.IndexOf(invalid, c) >= 0)
                buffer[i++] = '_';
            else
                buffer[i++] = c;
        }
        var sanitized = new string(buffer[..i]).Trim('.', ' ');
        return string.IsNullOrEmpty(sanitized) ? "app" : sanitized;
    }
}
