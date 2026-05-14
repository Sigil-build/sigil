using System;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace SigilBuild.Packaging.ExeWrapper;

/// <summary>
/// Builds the <c>SIGIL_INSTALLER_HOST_V1</c> resource payload: a zip archive
/// containing <c>installer.exe</c> (the AOT-published Avalonia wizard host)
/// plus its <c>BrandTokens.g.json</c> sidecar.
/// </summary>
/// <remarks>
/// <para>
/// The wrapper runtime reads this resource at install time (see
/// <c>InstallerHostLauncher</c>) and extracts it to a per-session temp directory
/// before launching the wizard. Bundling the host as a zip — rather than two
/// separate Win32 resources — keeps the extract code path simple (one stream,
/// ZipArchive, done) and means the host can grow to include additional
/// sidecars (license.rtf, hero.png, …) without touching the resource-name
/// vocabulary.
/// </para>
/// <para>
/// The exe is named <see cref="WizardEntryName"/> (sigil-wizard.exe) inside the
/// zip — deliberately NOT installer.exe — so Windows Installer Detection
/// (which heuristically prompts for UAC elevation on any exe whose filename
/// contains "install" / "setup" / "update") doesn't fire on the extracted
/// child. The wizard inherits elevation from the parent setup.exe (which IS
/// installer-detected and elevated on launch); a second UAC prompt is
/// unwanted and breaks Process.Start with UseShellExecute=false (Win32 740).
/// </para>
/// <para>
/// Avalonia's AOT publish output ships several native render-stack DLLs
/// alongside installer.exe (libSkiaSharp, libHarfBuzzSharp, av_libglesv2 on
/// Windows). All of them are bundled into the zip — without them the wizard
/// throws <c>DllNotFoundException</c> in <c>SkiaPlatform.Initialize</c> before
/// any window is shown. The bundler globs every <c>.dll</c> next to
/// installer.exe; <c>.pdb</c> files are intentionally skipped to keep the
/// bundle small.
/// </para>
/// </remarks>
internal static class InstallerHostBundle
{
    /// <summary>
    /// File name used inside the bundle zip for the extracted wizard exe.
    /// Avoids the "installer" / "setup" / "update" patterns that trigger
    /// Windows Installer Detection's auto-UAC heuristic.
    /// </summary>
    public const string WizardEntryName = "sigil-wizard.exe";

    /// <summary>
    /// File name used inside the bundle zip for the user's brand logo. The
    /// extension is whatever the source file had (.png / .jpg / .svg / …).
    /// The wizard's <c>BrandTokens.LogoFile</c> points at this name so
    /// runtime <c>Path.Combine(wizardDir, logoFile)</c> resolves correctly.
    /// </summary>
    public const string BrandLogoEntryPrefix = "brand-logo";

    /// <summary>
    /// Builds the zip blob bytes from a freshly-AOT-published installer.exe,
    /// every native sidecar DLL next to it (libSkiaSharp, libHarfBuzzSharp,
    /// av_libglesv2, …), a serialised <c>BrandTokens.g.json</c> body, an
    /// <c>InstallTimeParameters.g.json</c> describing every install-time
    /// parameter declared in the manifest, and (when present) the user's
    /// brand logo file bundled as <c>brand-logo.{ext}</c>.
    /// </summary>
    /// <param name="brandLogoSourcePath">Absolute path to the user's brand
    /// logo file (resolved by the caller from the manifest's
    /// <c>installer.brand.logo</c> relative path). Pass <c>null</c> when the
    /// manifest didn't declare a logo.</param>
    public static byte[] Build(string installerExePath, string brandTokensJson, string installTimeParametersJson, string? brandLogoSourcePath)
    {
        ArgumentNullException.ThrowIfNull(installerExePath);
        ArgumentNullException.ThrowIfNull(brandTokensJson);
        ArgumentNullException.ThrowIfNull(installTimeParametersJson);
        if (!File.Exists(installerExePath))
            throw new FileNotFoundException("installer.exe not found", installerExePath);

        var hostDir = Path.GetDirectoryName(installerExePath)
            ?? throw new InvalidOperationException(
                $"could not derive directory from installerExePath '{installerExePath}'");

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            // 1980-01-01 — deterministic mtime, same convention as
            // ExeWrapperPackager.BuildPayloadBytes so identical inputs produce
            // identical setup.exe byte streams across runs.
            var deterministicMtime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

            // 1) The wizard exe under its renamed-to-dodge-UAC entry name.
            AddFile(zip, installerExePath, WizardEntryName, deterministicMtime);

            // 2) Brand tokens — read at startup by the wizard's BrandTokens.LoadOrDefault.
            AddText(zip, "BrandTokens.g.json", brandTokensJson, deterministicMtime);

            // 3) Install-time parameter contract — read by the wizard to
            //    populate Install Options with the user's manifest defaults.
            AddText(zip, "InstallTimeParameters.g.json", installTimeParametersJson, deterministicMtime);

            // 4) User-supplied brand logo (when present). Bundling under a
            //    known name keeps the wizard's lookup deterministic: it reads
            //    BrandTokens.LogoFile and Path.Combines with its own dir. The
            //    extension is preserved so the wizard's loader can choose
            //    between Avalonia Bitmap (raster) and Svg.Skia (vector).
            if (brandLogoSourcePath is not null && File.Exists(brandLogoSourcePath))
            {
                var ext = Path.GetExtension(brandLogoSourcePath);
                var bundledName = BrandLogoEntryPrefix + ext;
                AddFile(zip, brandLogoSourcePath, bundledName, deterministicMtime);
            }

            // 5) Every native sidecar DLL next to installer.exe. Walks the
            //    publish directory in deterministic (sorted) order so the
            //    output bundle is byte-stable across runs.
            foreach (var dll in Directory.EnumerateFiles(hostDir, "*.dll").OrderBy(p => p, StringComparer.Ordinal))
            {
                AddFile(zip, dll, Path.GetFileName(dll), deterministicMtime);
            }
        }
        return ms.ToArray();
    }

    private static void AddText(ZipArchive zip, string entryName, string content, DateTimeOffset mtime)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        entry.LastWriteTime = mtime;
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, System.Text.Encoding.UTF8);
        writer.Write(content);
    }

    private static void AddFile(ZipArchive zip, string sourcePath, string entryName, DateTimeOffset mtime)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        entry.LastWriteTime = mtime;
        using var dst = entry.Open();
        using var src = File.OpenRead(sourcePath);
        src.CopyTo(dst);
    }
}
