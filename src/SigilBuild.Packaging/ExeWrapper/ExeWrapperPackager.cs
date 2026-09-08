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
/// with a step blob and the payload archive as Win32 resources (see ADR-008 —
/// docs/architecture/adr-008-expression-policy.md §5, closed step catalog +
/// deterministic stamping + wrapper-overhead cap).
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

    /// <summary>
    /// The <c>--payload embedded</c> (default) full package's file-name suffix —
    /// unchanged since T4.
    /// </summary>
    private const string SetupSuffix = "Setup";

    /// <summary>
    /// The <c>--payload web</c> stub's file-name suffix (T12.5): a clearly
    /// distinguishing name so a directory listing never confuses the tiny stub
    /// with the full package it downloads.
    /// </summary>
    private const string WebSetupSuffix = "WebSetup";

    public async Task<PackResult> PackAsync(SigilManifest manifest, PackOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(options);

        // The full package: built exactly as `--payload embedded` always has,
        // regardless of PayloadMode. For `--payload web` this is the artifact
        // hosted at options.PackageUrl; its sha256 (computed below, AFTER the
        // resource embed, same as always) is what the stub's synthesized
        // http_download step verifies against.
        var (fullArtifact, diagnostics) = await BuildOneExeAsync(
            manifest, options, SetupSuffix,
            blobBytesOverride: null, payloadBytesOverride: null, ct).ConfigureAwait(false);

        if (options.Payload != PayloadMode.Web || fullArtifact is null)
        {
            return new PackResult(fullArtifact, diagnostics);
        }

        // P12 (T12.5): synthesize the small stub. Its blob carries exactly two
        // install steps — reusing the EXISTING P4 http_download + run_program
        // step types, no new step catalog entry — and it carries NO app payload
        // (payloadBytesOverride: empty). Network only happens at INSTALL time
        // (as with any http_download); pack time here only computes the full
        // package's sha256 and embeds it as a literal, so two packs of the same
        // input + URL stay byte-identical.
        var stubBlobBytes = BuildWebStubBlobBytes(
            manifest, options.PackageUrl!, fullArtifact.Sha256, Path.GetFileName(fullArtifact.Path));

        var (stubArtifact, stubDiagnostics) = await BuildOneExeAsync(
            manifest, options, WebSetupSuffix,
            blobBytesOverride: stubBlobBytes, payloadBytesOverride: Array.Empty<byte>(), ct)
            .ConfigureAwait(false);

        var allDiagnostics = new List<Diagnostic>(diagnostics);
        allDiagnostics.AddRange(stubDiagnostics);

        return new PackResult(fullArtifact, allDiagnostics, SecondaryArtifact: stubArtifact);
    }

    /// <summary>
    /// Build one stamped <c>&lt;App&gt;-&lt;ver&gt;-&lt;arch&gt;-&lt;suffix&gt;.exe</c>
    /// artifact: copy the AOT host runtime, embed the blob + payload + native
    /// runtime deps as Win32 resources, stamp the icon, then hash the result.
    /// Shared by both the normal <c>--payload embedded</c> pack (suffix
    /// <c>Setup</c>, no overrides) and the <c>--payload web</c> stub (suffix
    /// <c>WebSetup</c>, a synthesized blob + an empty payload override) — see
    /// <see cref="PackAsync"/>.
    /// </summary>
    private static async Task<(PackedArtifact? Artifact, List<Diagnostic> Diagnostics)> BuildOneExeAsync(
        SigilManifest manifest,
        PackOptions options,
        string outputNameSuffix,
        byte[]? blobBytesOverride,
        byte[]? payloadBytesOverride,
        CancellationToken ct)
    {
        // Locate the AOT-published host runtime for the target architecture.
        // A manifest declaring architectures: [x64, arm64] produces one Setup.exe
        // per architecture, each stamped from the matching per-RID runtime.
        // Surface the missing-runtime case as a SIG0120 diagnostic (PR #8) — the
        // dev workflow expects build-wrappers to stage the AOT exe alongside the
        // SDK before pack-time — rather than throwing deep in the packager.
        string stubPath;
        try
        {
            stubPath = WrapperRuntimeLocator.Locate(options.Architecture);
        }
        catch (FileNotFoundException ex)
        {
            return (null, new List<Diagnostic>
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
        var outputName = $"{safeName}-{manifest.App.Version}-{archStr}-{outputNameSuffix}.exe";
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
        // with the wrapper runtime. A non-null override (the web stub's
        // synthesized blob) is used verbatim instead.
        var blobBytes = blobBytesOverride ?? BuildBlobBytes(manifest, options.SourceDirectory, diagnostics);

        // Build the SIGIL_PAYLOAD_V2 wire payload: the source directory packed
        // into the deterministic zstd container (T6), decoded on the host side by
        // PayloadExtraction. The codec is shared with the future delta-update
        // engine (spec section 5). A non-null override (empty, for the web
        // stub — it carries NO app payload) is used verbatim instead.
        var payloadBytes = payloadBytesOverride ?? BuildPayloadBytes(options.SourceDirectory, ct);

        // T18: archive the host's staged native dependencies (Skia/ANGLE/HarfBuzz)
        // so the stamped Setup.exe is self-contained and can launch the GUI wizard
        // standalone. Empty when no natives are staged (SIGIL_RUNTIME_V1 is then
        // simply not written — the pre-T18 exe-only behaviour, still /silent-capable).
        var nativeDepPaths = WrapperRuntimeLocator.LocateNativeDeps(options.Architecture);
        var runtimeBytes = BuildRuntimeBytes(nativeDepPaths, ct);

        // Resolve the installer icon (PR #8) up front — stamped into the produced
        // setup.exe's Explorer/Shell icon after the resource-update cycle below.
        // Null when neither a manifest installer.icon nor the bundled default is
        // readable (the wrapper then keeps its stock icon).
        var iconBytes = ResolveIconBytes(manifest);

        // Embed the resources via the Win32 update-resource flow.
        await WrapperResourceWriter.WriteAsync(outputPath, blobBytes, payloadBytes, runtimeBytes, ct)
            .ConfigureAwait(false);

        // Stamp the icon AFTER WrapperResourceWriter has finished its
        // BeginUpdateResource / EndUpdateResource cycle. Concurrent updates on
        // the same PE file are not safe. Same bytes as the wizard-bundle stamp
        // so setup.exe and the wizard window share one branded icon.
        if (iconBytes is not null)
        {
            await IconResourceWriter.WriteAsync(outputPath, iconBytes, ct).ConfigureAwait(false);
        }

        // Compute sha256 + size *after* the resource embed so the artifact
        // descriptor reflects the final on-disk shape.
        var sha256 = ManifestHasher.Sha256(outputPath);
        var size = new FileInfo(outputPath).Length;

        return (new PackedArtifact(outputPath, sha256, size), diagnostics);
    }

    /// <summary>
    /// Synthesize the web-installer stub's <c>SIGIL_BLOB_V1</c> bytes (P12,
    /// T12.5): exactly two install steps, reusing the EXISTING catalog —
    /// <list type="number">
    ///   <item><description>an <see cref="InstallStep.HttpDownload"/> of
    ///   <paramref name="packageUrl"/> to <c>{staging_dir}/&lt;fullPackageFileName&gt;</c>,
    ///   verified against <paramref name="packageSha256"/> (the just-built full
    ///   package's actual sha256 — computed by the caller AFTER the resource
    ///   embed, so this is never a guess);</description></item>
    ///   <item><description>an <see cref="InstallStep.RunProgram"/> of that same
    ///   downloaded path, waited-on, running the full package headlessly
    ///   (<c>/verysilent</c>) so the stub's own progress screen is the only UI
    ///   the user sees during the hand-off.</description></item>
    /// </list>
    /// Deterministic: every input is a literal pack-time value (URL, sha256, file
    /// name, app metadata) — no timestamp, GUID, or other non-reproducible byte
    /// ever enters this blob, so two packs of the same manifest + URL are
    /// byte-identical (network happens only at INSTALL time, resolving
    /// <c>{temp_dir}</c> then — see <see cref="Engine.StepContext"/>).
    /// </summary>
    // CA1861: hoisted out of BuildWebStubBlobBytes so the RunProgram step's
    // fixed `/verysilent` argument isn't a fresh array literal on every call.
    private static readonly string[] RunSilentArgs = { "/verysilent" };

    // CA1861: hoisted for the same reason. 0 = clean success; 3010 = success,
    // reboot required (the standard MSI/Windows-installer convention the full
    // package's own prerequisite/step handling already honors) — the child
    // Setup.exe returning 3010 must not spuriously fail the stub's run_program
    // (RunProgramStep otherwise defaults an unset ExpectedExitCodes to [0] only).
    private static readonly int[] RunExpectedExitCodes = { 0, 3010 };

    internal static byte[] BuildWebStubBlobBytes(
        SigilManifest manifest, string packageUrl, string packageSha256, string fullPackageFileName)
    {
        // R5: NOT "{temp_dir}/" + fullPackageFileName. That was a pack-time constant
        // derived from the public artifact name — every copy of the stub named the same
        // predictable path in the shared per-user %TEMP% root, so any process running as
        // the same user could pre-plant that file before the download and swap it
        // afterwards, between the checksum and the `requireAdministrator` launch.
        // {staging_dir} resolves at INSTALL time to a freshly created GUID-named private
        // directory (administrator-only when elevated), so the blob stays a deterministic
        // literal while the actual path is unguessable and per-run. The second half of
        // the fix is in the engine: run_program re-verifies a file this run downloaded,
        // from a handle held across the launch.
        var downloadDest = "{staging_dir}/" + fullPackageFileName;

        var installSteps = new InstallStep[]
        {
            new InstallStep.HttpDownload(
                Id: "web_installer_download",
                Url: packageUrl,
                Dest: downloadDest,
                Sha256: packageSha256,
                TimeoutSeconds: null,
                Retries: 3,
                When: null,
                OnFailure: OnFailure.Fail)
            {
                // R16: every step destination is contained to install_dir. This
                // one deliberately is not — the stub downloads the full package to
                // a temp location and hands off to it; the stub itself installs
                // nothing into install_dir. The download is SHA-256-verified
                // before it is executed, which is what makes an out-of-tree
                // destination defensible here, and stating it explicitly is the
                // whole point of the opt-out being per-step and declared.
                AllowOutsideInstallDir = true,
            },
            new InstallStep.RunProgram(
                Id: "web_installer_run",
                Program: downloadDest,
                Args: RunSilentArgs,
                Wait: true,
                Cwd: null,
                ExpectedExitCodes: RunExpectedExitCodes,
                TimeoutSeconds: null,
                When: null,
                OnFailure: OnFailure.Fail),
        };

        var inMemory = new WrapperBlob(
            AppId: manifest.App.Id,
            Parameters: Array.Empty<ParameterDefinition>(),
            InstallSteps: installSteps,
            PreInstall: Array.Empty<InstallStep>(),
            PostInstall: Array.Empty<InstallStep>(),
            UpdateSteps: Array.Empty<InstallStep>(),
            Scope: manifest.Installer?.Scope ?? InstallScope.Auto,
            AppName: manifest.App.Name,
            DisplayName: manifest.App.Name,
            Publisher: manifest.App.Publisher,
            Version: manifest.App.Version,
            EstimatedSizeBytes: 0,
            // P12 (T12.5): mark this blob as a pure delegating trampoline so
            // InstallSession skips its OWN completion bookkeeping on success —
            // see WrapperBlob.IsDelegatingStub for why. The embedded-payload
            // path (BuildBlobBytes) never sets this, so it stays false there.
            IsDelegatingStub: true);

        var serializable = SerializableWrapperBlob.FromWrapperBlob(inMemory) with
        {
            // T11 / decision 7: the stub is its own artifact — its trust line is
            // gated on whether ITS OWN pack declares signing, same rule as the
            // full package (see BuildBlobBytes). Both artifacts get signed
            // independently by `sigil sign` after packing.
            SignDeclared = manifest.Sign is { Provider: not SignProvider.None },
            // R45: the stub downloads and runs the real package, so it is one of the
            // two call sites the policy governs. Carry the DECLARED value rather than
            // letting the runtime infer it from SignDeclared.
            RequireSignedDownloads = manifest.Installer?.RequireSignedDownloads
                ?? RequireSignedDownloads.SignDeclared,
        };

        var json = System.Text.Json.JsonSerializer.Serialize(
            serializable, WrapperBlobJsonContext.Default.SerializableWrapperBlob);
        return System.Text.Encoding.UTF8.GetBytes(json);
    }

    /// <summary>
    /// Resolve the bytes of the installer icon to stamp into the produced
    /// setup.exe. Precedence:
    ///   1. <c>installer.icon</c> in the manifest, resolved relative to the
    ///      manifest file's directory.
    ///   2. The bundled default icon (embedded as
    ///      <c>SigilBuild.Packaging.DefaultInstallerIcon.ico</c>).
    /// Returns the icon bytes, or <c>null</c> when neither path is readable
    /// (callers skip the icon-stamp step and leave the wrapper's stock icon).
    /// </summary>
    private static byte[]? ResolveIconBytes(SigilManifest manifest)
    {
        var userIcon = manifest.Installer?.Icon;
        if (!string.IsNullOrEmpty(userIcon))
        {
            var manifestDir = Path.GetDirectoryName(manifest.Location.File);
            var candidate = Path.IsPathRooted(userIcon) || string.IsNullOrEmpty(manifestDir)
                ? userIcon
                : Path.GetFullPath(Path.Combine(manifestDir, userIcon));
            if (File.Exists(candidate)) return File.ReadAllBytes(candidate);
        }

        var asm = typeof(ExeWrapperPackager).Assembly;
        using var s = asm.GetManifestResourceStream("SigilBuild.Packaging.DefaultInstallerIcon.ico");
        if (s is null) return null;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }


    internal static byte[] BuildBlobBytes(
        SigilManifest manifest, string sourceDirectory, ICollection<Diagnostic>? diagnostics = null)
    {
        var parameters = manifest.Parameters is null
            ? Array.Empty<ParameterDefinition>()
            : ParametersToList(manifest.Parameters);

        // T8: for each ENABLED built-in option component, auto-generate its install
        // step(s), each gated on `option.<component>`. A disabled component yields
        // nothing. The generated steps run AFTER the manifest's own install_steps
        // (shortcuts / PATH / associations follow the file copies), and the ENABLED
        // component list is carried in the blob so the runtime can seed `option.*`
        // and the host can render the Options screen.
        var (optionSteps, optionComponents) =
            OptionStepGenerator.Generate(manifest.Installer?.Options, manifest.App);
        var installSteps = CombineSteps(manifest.InstallSteps, optionSteps);

        var inMemory = new WrapperBlob(
            AppId: manifest.App.Id,
            Parameters: parameters,
            InstallSteps: installSteps,
            PreInstall: manifest.PreInstall ?? Array.Empty<InstallStep>(),
            PostInstall: manifest.PostInstall ?? Array.Empty<InstallStep>(),
            // Update-step block doesn't yet exist on SigilManifest (Task 19+);
            // emit an empty list for forward compatibility.
            UpdateSteps: Array.Empty<InstallStep>(),
            // T12: carry the manifest's install scope (user | machine | auto) into
            // the blob so the runtime can resolve the effective scope (against the
            // /allusers /currentuser flags) and elevate when a machine install is
            // requested from a non-elevated process.
            Scope: manifest.Installer?.Scope ?? InstallScope.Auto,
            // T8: the enabled option components the runtime + wizard consume.
            Options: optionComponents,
            // T13: carry App.Name (the default install-dir base + {app.name} token)
            // and the optional install_dir override template into the blob so the
            // runtime resolves the effective install dir (default / manifest / /D=)
            // and the {install_dir} token in step paths + expressions.
            AppName: manifest.App.Name,
            InstallDir: manifest.Installer?.InstallDir,
            // T10: the real Add/Remove Programs fields. DisplayName/Publisher/Version
            // come straight from manifest.App.*; EstimatedSizeBytes is the installed
            // footprint estimate — the uncompressed payload size (the bytes that land
            // on disk once the zstd container is extracted), which is what ARP's size
            // column is meant to reflect. The runtime registers these instead of the
            // former AppId / "1.0.0" / "Unknown" / 0 placeholders.
            DisplayName: manifest.App.Name,
            Publisher: manifest.App.Publisher,
            Version: manifest.App.Version,
            EstimatedSizeBytes: ComputeInstalledSizeBytes(sourceDirectory),
            // P1: carry the declarative installer.vars (name → expression) into the
            // blob so the runtime can evaluate them once at session start and seed
            // var.<name>. Order-preserving; cycles were rejected at parse time.
            Vars: manifest.Installer?.Vars,
            // P2 (gap G2): lifecycle hooks that run outside the journal.
            HookPreInstall: manifest.Installer?.Hooks?.PreInstall,
            HookPostInstall: manifest.Installer?.Hooks?.PostInstall,
            HookPreUninstall: manifest.Installer?.Hooks?.PreUninstall,
            HookPostUninstall: manifest.Installer?.Hooks?.PostUninstall,
            // P2 (gap G4): the Done-screen "Launch <App>" target.
            RunAfterInstallPath: manifest.Installer?.RunAfterInstall?.Path,
            RunAfterInstallArgs: manifest.Installer?.RunAfterInstall?.Args,
            // P5 (gap G6): first-class prerequisite units, run before the journaled body.
            Prerequisites: manifest.Installer?.Prerequisites,
            // P6 (gap G7): the declared app mutex names.
            AppMutex: manifest.Installer?.AppMutex,
            // P12 (T12.3): the updates: metadata (manifestUrl / signingKey / channel)
            // so the stamped /Update runtime can fetch + verify the signed channel
            // manifest and decide whether a newer package is available. Null when the
            // manifest declares no updates: block (the app is not update-enabled).
            UpdateManifestUrl: manifest.Updates?.ManifestUrl,
            UpdateSigningKey: manifest.Updates?.SigningKey,
            UpdateChannel: manifest.Updates?.Channel);

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
            // T14 / P9 (gap G10): read each manifest-referenced license file and
            // embed a tag -> text map so the stamped host can show the License
            // screen in the resolved language. Missing/unreadable/empty entries
            // drop out (SIG0250, non-fatal); a non-empty result missing 'en' is
            // fatal (SIG0290, see ReadLicenseText). An entirely empty result is
            // null (the screen is simply omitted — T14's original behavior).
            LicenseText = ReadLicenseText(manifest.Installer?.License, sourceDirectory, diagnostics),
            // T11 / decision 7: mark the artifact as INTENDED to be signed iff the
            // manifest declares a real `sign` block. The trust line is gated at
            // install time on SignDeclared && WinVerifyTrust(self) == valid — never
            // on App.publisher alone — so an unsigned pack (or one whose signature
            // is later invalidated) never shows "Signed by …". Pipeline ordering:
            // pack stamps resources FIRST (invalidating any prior signature), then
            // `sigil sign` signs the finished Setup.exe LAST.
            SignDeclared = manifest.Sign is { Provider: not SignProvider.None },
            // P9 (gap G10): the manifest's optional fixed installer language, carried
            // like Screens/LicenseText above — a session-bootstrap concern read
            // straight off SerializableWrapperBlob, not part of the in-memory
            // WrapperBlob the engine steps operate on.
            Language = manifest.Installer?.Language,
            // R45: the declared downloaded-binary signature policy, replacing the
            // runtime's inference from SignDeclared. Default is that same inference.
            RequireSignedDownloads = manifest.Installer?.RequireSignedDownloads
                ?? RequireSignedDownloads.SignDeclared,
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
    /// Reads each declared license file (<c>installer.license</c>, T14 / P9 gap
    /// G10) at pack time into a tag -&gt; text map. Ownership (design §5.3):
    /// <see cref="DiagnosticCodes.LicenseFileUnreadable"/> (SIG0250) owns
    /// per-file readability and is non-fatal — that entry simply drops out of
    /// the map; <see cref="DiagnosticCodes.LocalizedTextMissingEnglish"/>
    /// (SIG0290) owns the <c>en</c> invariant and is fatal. The invariant is
    /// asserted on the POST-READ map, so <c>{en: missing.txt, uk: ok.txt}</c>
    /// fails via SIG0290 rather than silently packing a license only Ukrainian
    /// users can read. An empty result (every entry dropped) returns
    /// <c>null</c> and omits the screen — T14's original behavior, unchanged.
    /// </summary>
    private static Dictionary<string, string>? ReadLicenseText(
        LocalizedText? license, string sourceDirectory, ICollection<Diagnostic>? diagnostics)
    {
        if (license is null)
        {
            return null;
        }

        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (tag, pathOrText) in license.Values)
        {
            var text = ReadOneLicense(pathOrText, sourceDirectory, tag, diagnostics);
            if (!string.IsNullOrWhiteSpace(text))
            {
                texts[tag] = text!;
            }
        }

        if (texts.Count == 0)
        {
            return null; // T14: no readable text -> no License screen. Not a SIG0290 case.
        }

        if (!texts.ContainsKey("en"))
        {
            diagnostics?.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                DiagnosticCodes.LocalizedTextMissingEnglish,
                $"installer.license has no readable 'en' entry (found: {string.Join(", ", texts.Keys)}). " +
                "Every localized value needs an English fallback — without it there is no defined " +
                "rendering for users whose language you do not ship.",
                SourceLocation.Unknown,
                "https://docs.sigil.build/diagnostics/SIG0290"));
        }

        return texts;
    }

    /// <summary>
    /// Reads a single declared license file as text, resolving a relative path
    /// against the pack source directory. Returns <c>null</c> — and appends a
    /// non-fatal <see cref="DiagnosticCodes.LicenseFileUnreadable"/> warning to
    /// <paramref name="diagnostics"/> — when the path is set but the file is
    /// missing, unreadable, or empty. Per T14 this never hard-fails the pack by
    /// itself; the caller (<see cref="ReadLicenseText"/>) decides whether the
    /// resulting map still needs the fatal SIG0290 check. Plain text only
    /// (RTF-as-text v1; no RTF parsing). <paramref name="tag"/> identifies which
    /// language entry this is, for callers building a tag -&gt; text map.
    /// </summary>
    private static string? ReadOneLicense(
        string? path, string sourceDirectory, string tag, ICollection<Diagnostic>? diagnostics)
    {
        _ = tag; // identifies the entry to the caller; the SIG0250 message itself is per-file, not per-language.

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

    /// <summary>
    /// Append the pack-time-generated option steps (T8) after the manifest's own
    /// <c>install_steps</c>, preserving order. Returns the manifest list unchanged
    /// when no option steps were generated, and an empty list when both are empty.
    /// </summary>
    private static IReadOnlyList<InstallStep> CombineSteps(
        IReadOnlyList<InstallStep>? manifestSteps, IReadOnlyList<InstallStep> generated)
    {
        var baseSteps = manifestSteps ?? Array.Empty<InstallStep>();
        if (generated.Count == 0)
        {
            return baseSteps;
        }

        var combined = new List<InstallStep>(baseSteps.Count + generated.Count);
        combined.AddRange(baseSteps);
        combined.AddRange(generated);
        return combined;
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
    /// Estimate the installed footprint (T10) reported to Add/Remove Programs as
    /// <c>EstimatedSize</c>. Defined as the sum of the <em>uncompressed</em> payload
    /// file sizes — the bytes that actually land on disk once the zstd container is
    /// extracted — which is a truer reflection of the on-disk footprint than the
    /// compressed Setup.exe size. Deterministic (file order does not affect the sum),
    /// and <c>0</c> when the source directory is absent/empty. <c>ArpRegistration</c>
    /// converts bytes → KB for the OS.
    /// </summary>
    internal static long ComputeInstalledSizeBytes(string sourceDirectory)
    {
        if (string.IsNullOrEmpty(sourceDirectory) || !Directory.Exists(sourceDirectory))
        {
            return 0;
        }

        long total = 0;
        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            total += new FileInfo(file).Length;
        }
        return total;
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
