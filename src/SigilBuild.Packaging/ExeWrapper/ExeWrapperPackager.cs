using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SigilBuild.Core.Diagnostics;
using SigilBuild.Core.Manifest;
using SigilBuild.Packaging.Common;
using SigilBuild.Packaging.Installer;
using SigilBuild.Wrapper.Codec;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Json;

namespace SigilBuild.Packaging.ExeWrapper;

/// <summary>
/// Packs an application as a single self-extracting <c>.exe</c> wrapper —
/// the Native-AOT-published <c>SigilBuild.Installer.Host</c> runtime, stamped
/// with a step blob and the payload archive as Win32 resources (see ADR-008).
/// </summary>
/// <remarks>
/// Task 14 implementation: the packager copies the stub runtime to
/// <c>{OutputDirectory}/{App.Name}-Setup.exe</c>, then embeds the JSON
/// step + parameter blob (<c>SIGIL_BLOB_V1</c>) and a deterministic zstd
/// container of the source directory (<c>SIGIL_PAYLOAD_V2</c>, T6) as Win32
/// <c>RT_RCDATA</c> resources via <see cref="WrapperResourceWriter"/>.
/// </remarks>
public sealed class ExeWrapperPackager : IPackager
{
    public PackageFormat Format => PackageFormat.Exe;

    public async Task<PackResult> PackAsync(SigilManifest manifest, PackOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(options);

        // Locate the AOT-published host runtime for the target architecture.
        // A manifest declaring architectures: [x64, arm64] produces one Setup.exe
        // per architecture, each stamped from the matching per-RID runtime.
        var stubPath = WrapperRuntimeLocator.Locate(options.Architecture);

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

        // Diagnostics surfaced during blob construction (T14: a missing /
        // unreadable / empty license file is a non-fatal warning — the pack
        // succeeds and the License screen is simply omitted).
        var diagnostics = new List<Diagnostic>();

        // Build the SIGIL_BLOB_V1 wire payload: serialize a
        // SerializableWrapperBlob via the source-generated context shared
        // with the wrapper runtime.
        var blobBytes = BuildBlobBytes(manifest, options.SourceDirectory, diagnostics);

        // Build the SIGIL_PAYLOAD_V2 wire payload: the source directory packed
        // into the deterministic zstd container (T6), decoded on the host side by
        // PayloadExtraction. The codec is shared with the future delta-update
        // engine (spec section 5).
        var payloadBytes = BuildPayloadBytes(options.SourceDirectory, ct);

        // T18: archive the host's staged native dependencies (Skia/ANGLE/HarfBuzz)
        // so the stamped Setup.exe is self-contained and can launch the GUI wizard
        // standalone. Empty when no natives are staged (SIGIL_RUNTIME_V1 is then
        // simply not written — the pre-T18 exe-only behaviour, still /silent-capable).
        var nativeDepPaths = WrapperRuntimeLocator.LocateNativeDeps(options.Architecture);
        var runtimeBytes = BuildRuntimeBytes(nativeDepPaths, ct);

        // Embed the resources via the Win32 update-resource flow.
        await WrapperResourceWriter.WriteAsync(outputPath, blobBytes, payloadBytes, runtimeBytes, ct)
            .ConfigureAwait(false);

        // Compute sha256 + size *after* the resource embed so the artifact
        // descriptor reflects the final on-disk shape.
        var sha256 = ManifestHasher.Sha256(outputPath);
        var size = new FileInfo(outputPath).Length;

        var artifact = new PackedArtifact(outputPath, sha256, size);
        return new PackResult(artifact, diagnostics);
    }

    internal static byte[] BuildBlobBytes(
        SigilManifest manifest, string sourceDirectory, ICollection<Diagnostic>? diagnostics = null)
    {
        var parameters = manifest.Parameters is null
            ? Array.Empty<ParameterDefinition>()
            : ParametersToList(manifest.Parameters);

        var inMemory = new WrapperBlob(
            AppId: manifest.App.Id,
            Parameters: parameters,
            InstallSteps: manifest.InstallSteps ?? Array.Empty<InstallStep>(),
            PreInstall: manifest.PreInstall ?? Array.Empty<InstallStep>(),
            PostInstall: manifest.PostInstall ?? Array.Empty<InstallStep>(),
            // Update-step block doesn't yet exist on SigilManifest (Task 19+);
            // emit an empty list for forward compatibility.
            UpdateSteps: Array.Empty<InstallStep>(),
            // T12: carry the manifest's install scope (user | machine | auto) into
            // the blob so the runtime can resolve the effective scope (against the
            // /allusers /currentuser flags) and elevate when a machine install is
            // requested from a non-elevated process.
            Scope: manifest.Installer?.Scope ?? InstallScope.Auto);

        // T7: derive the full light/dark palette at pack time and carry it, plus
        // the base64 logo/hero bytes, inside the blob so the stamped exe renders
        // branded with no loose files beside it (decision 11).
        var palette = BrandTokenEmitter.Derive(manifest);
        var brand = manifest.Installer?.Brand;

        var serializable = SerializableWrapperBlob.FromWrapperBlob(inMemory) with
        {
            BrandTokensLight = new Dictionary<string, string>(palette.Light),
            BrandTokensDark = new Dictionary<string, string>(palette.Dark),
            LogoBase64 = ReadImageBase64(brand?.Logo, sourceDirectory),
            HeroBase64 = ReadImageBase64(brand?.Hero, sourceDirectory),
            // T9: carry the declared custom wizard screens into the blob so the
            // stamped host can render them. Parameters are already populated by
            // FromWrapperBlob; screens live only on the manifest's InstallerSection.
            Screens = ScreensToArray(manifest.Installer?.Screens),
            // T14: read the manifest-referenced license file and embed its text so
            // the stamped host shows the License screen. Missing/unreadable/empty
            // → null + a non-fatal diagnostic (the screen is simply omitted).
            LicenseText = ReadLicenseText(manifest.Installer?.License, sourceDirectory, diagnostics),
        };

        var json = System.Text.Json.JsonSerializer.Serialize(
            serializable, WrapperBlobJsonContext.Default.SerializableWrapperBlob);
        return System.Text.Encoding.UTF8.GetBytes(json);
    }

    /// <summary>
    /// Reads a manifest brand image (logo/hero) as base64, resolving a relative
    /// path against the pack source directory. Returns <c>null</c> when the path
    /// is unset or the file is missing — brand assets are optional and their
    /// absence must not fail the pack.
    /// </summary>
    private static string? ReadImageBase64(string? path, string sourceDirectory)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var resolved = Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(Path.Combine(sourceDirectory ?? string.Empty, path));

        if (!File.Exists(resolved))
            return null;

        return Convert.ToBase64String(File.ReadAllBytes(resolved));
    }

    /// <summary>
    /// Reads the manifest-referenced license file (<c>installer.license</c>, T14)
    /// as text, resolving a relative path against the pack source directory.
    /// Returns <c>null</c> — and appends a non-fatal <see cref="DiagnosticCodes.LicenseFileUnreadable"/>
    /// warning to <paramref name="diagnostics"/> — when the path is set but the
    /// file is missing, unreadable, or empty. Per T14 this never hard-fails the
    /// pack; the License screen is simply omitted. Plain text only (RTF-as-text v1;
    /// no RTF parsing).
    /// </summary>
    private static string? ReadLicenseText(
        string? path, string sourceDirectory, ICollection<Diagnostic>? diagnostics)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var resolved = Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(Path.Combine(sourceDirectory ?? string.Empty, path));

        string text;
        try
        {
            if (!File.Exists(resolved))
            {
                ReportLicense(diagnostics,
                    $"installer license file not found: '{path}' (resolved to '{resolved}') — the License screen will be omitted");
                return null;
            }

            text = File.ReadAllText(resolved);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            ReportLicense(diagnostics,
                $"installer license file could not be read: '{path}' — {ex.Message}; the License screen will be omitted");
            return null;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            ReportLicense(diagnostics,
                $"installer license file is empty: '{path}' — the License screen will be omitted");
            return null;
        }

        return text;
    }

    private static void ReportLicense(ICollection<Diagnostic>? diagnostics, string message)
    {
        diagnostics?.Add(new Diagnostic(
            DiagnosticSeverity.Warning,
            DiagnosticCodes.LicenseFileUnreadable,
            message,
            SourceLocation.Unknown,
            "https://docs.sigil.build/diagnostics/SIG0250"));
    }

    private static SigilBuild.Wrapper.Json.SerializableInstallerScreen[] ScreensToArray(
        IReadOnlyList<InstallerScreen>? screens)
    {
        if (screens is null || screens.Count == 0)
        {
            return Array.Empty<SigilBuild.Wrapper.Json.SerializableInstallerScreen>();
        }
        var arr = new SigilBuild.Wrapper.Json.SerializableInstallerScreen[screens.Count];
        for (var i = 0; i < screens.Count; i++)
        {
            arr[i] = SigilBuild.Wrapper.Json.SerializableInstallerScreen.FromInstallerScreen(screens[i]);
        }
        return arr;
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

    /// <summary>
    /// Build the <c>SIGIL_PAYLOAD_V2</c> wire payload: the source directory
    /// packed into the deterministic zstd container defined by
    /// <see cref="PayloadCodec"/> (T6). Every file is enumerated, read, and handed
    /// to the codec, which sorts entries by ordinal relative path and stores no
    /// timestamps — so the container, and therefore the stamped Setup.exe, is
    /// byte-identical across builds of the same input. The host decompresses the
    /// same container via the same codec (see <c>PayloadExtraction</c>), and the
    /// future delta-update engine reuses it (spec section 5).
    /// </summary>
    internal static byte[] BuildPayloadBytes(string sourceDirectory, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(sourceDirectory) || !Directory.Exists(sourceDirectory))
        {
            return Array.Empty<byte>();
        }

        var root = Path.GetFullPath(sourceDirectory);
        var entries = new List<PayloadEntry>();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            entries.Add(new PayloadEntry(rel, File.ReadAllBytes(file)));
        }

        return PayloadCodec.Encode(entries);
    }

    /// <summary>
    /// Archive the staged native-dependency DLLs (T18) into the deterministic zip
    /// container carried by <c>SIGIL_RUNTIME_V1</c>. Entries are flat file names
    /// (the runtime bootstrap loads every DLL from one search directory), sorted
    /// ordinal, with a pinned mtime — so the archive, and therefore the stamped
    /// Setup.exe, is byte-identical across builds. Returns an empty array when no
    /// native deps are supplied; the writer then omits the resource entirely.
    /// </summary>
    internal static byte[] BuildRuntimeBytes(IReadOnlyList<string> nativeDependencyPaths, CancellationToken ct)
    {
        if (nativeDependencyPaths is null || nativeDependencyPaths.Count == 0)
        {
            return Array.Empty<byte>();
        }

        // Store by (deduplicated) file name, sorted for determinism regardless of
        // the caller's enumeration order.
        var ordered = nativeDependencyPaths
            .Select(p => (Name: Path.GetFileName(p), Path: p))
            .GroupBy(e => e.Name, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .ToArray();

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, path) in ordered)
            {
                ct.ThrowIfCancellationRequested();
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                entry.LastWriteTime = DeterministicMtime;
                using var entryStream = entry.Open();
                using var fs = File.OpenRead(path);
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
