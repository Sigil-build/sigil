using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using SigilBuild.Core.Configuration;
using SigilBuild.Core.Diagnostics;
using SigilBuild.Core.Manifest;
using SigilBuild.Packaging;
using SigilBuild.Packaging.ExeWrapper;
using SigilBuild.Packaging.Msix;
using SigilBuild.Packaging.Zip;

namespace SigilBuild.Cli.Commands;

public static class PackCommand
{
    public static Command Build()
    {
        var pathArg = new Argument<string>("path", () => "sigil.yaml", "Path to the manifest");
        var outOpt = new Option<string>("--out", () => "./dist", "Output directory");
        // P12 (T12.5): the exe format's payload delivery mode. "embedded" (default)
        // is the original, unchanged behavior — the app payload is stamped straight
        // into the Setup.exe. "web" instead emits the normal full package (hosted at
        // --package-url) PLUS a small stub Setup.exe whose only install action
        // downloads + runs it (Burn/NSIS-style web installer).
        var payloadOpt = new Option<string>(
            "--payload", () => "embedded", "Exe payload delivery: embedded | web");
        var packageUrlOpt = new Option<string?>(
            "--package-url",
            "HTTPS URL the full package will be hosted at — REQUIRED when --payload web");

        var cmd = new Command(
            "pack",
            "Pack the app per the manifest. Note: the 'exe' installer format is produced " +
            "only on a Windows pack host — it stamps the payload into the installer runtime " +
            "via the Win32 resource-update APIs (BeginUpdateResourceW), which have no " +
            "cross-platform equivalent. On non-Windows hosts 'sigil pack' emits a clear " +
            "diagnostic and skips the exe format (other formats still pack). Pipeline order: " +
            "'sigil pack' MUST run BEFORE 'sigil sign' — stamping resources invalidates any " +
            "prior Authenticode signature, so sign the finished Setup.exe last.");
        cmd.AddArgument(pathArg);
        cmd.AddOption(outOpt);
        cmd.AddOption(payloadOpt);
        cmd.AddOption(packageUrlOpt);
        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var path = ctx.ParseResult.GetValueForArgument(pathArg);
            var outDir = ctx.ParseResult.GetValueForOption(outOpt) ?? "./dist";
            var payloadRaw = ctx.ParseResult.GetValueForOption(payloadOpt) ?? "embedded";
            var packageUrl = ctx.ParseResult.GetValueForOption(packageUrlOpt);
            var isWebPayload = string.Equals(payloadRaw, "web", StringComparison.OrdinalIgnoreCase);

            // T12.5: `--payload web` requires a resolvable (https-only) package URL —
            // that is where the stub's synthesized http_download step fetches the full
            // package from at install time. Fail fast, before touching the manifest.
            if (isWebPayload &&
                (string.IsNullOrWhiteSpace(packageUrl) ||
                 !packageUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                DiagnosticReporter.Write(Console.Error, new[]
                {
                    new Diagnostic(
                        DiagnosticSeverity.Error,
                        DiagnosticCodes.WebInstallerPackageUrlUnresolved,
                        "pack --payload web requires a resolvable HTTPS --package-url (missing, " +
                        $"empty, or non-https URLs are refused) — got '{packageUrl ?? "<none>"}'.",
                        SourceLocation.Unknown,
                        "https://docs.sigil.build/diagnostics/SIG0322"),
                }, useColor: false);
                ctx.ExitCode = 1;
                return;
            }

            var load = await ManifestLoader.LoadAsync(path, new ProcessEnvironmentReader());
            DiagnosticReporter.Write(Console.Error, load.Diagnostics, useColor: false);
            if (load.Manifest is null) { ctx.ExitCode = 1; return; }

            Directory.CreateDirectory(outDir);
            var manifest = load.Manifest;
            var formats = manifest.Package?.Formats ?? new[] { PackageFormat.Zip };
            var arches = manifest.Package?.Architectures ?? new[] { TargetArchitecture.X64 };

            foreach (var format in formats)
            {
                // The exe wrapper is a Windows-only pack target: WrapperResourceWriter
                // embeds the payload via the Win32 BeginUpdateResourceW/UpdateResourceW/
                // EndUpdateResourceW flow, which has no cross-platform equivalent. Emit a
                // clear diagnostic and skip the format rather than crashing deep inside
                // the packager with an obscure P/Invoke failure. Other declared formats
                // still pack; the non-zero exit code flags the unmet request.
                if (format == PackageFormat.Exe && !OperatingSystem.IsWindows())
                {
                    DiagnosticReporter.Write(Console.Error, new[]
                    {
                        new Diagnostic(
                            DiagnosticSeverity.Error,
                            "SIG0270",
                            "package format 'exe' can only be produced on a Windows pack host — " +
                            "it stamps the installer payload via the Win32 resource-update APIs " +
                            "(BeginUpdateResourceW), which have no cross-platform equivalent. " +
                            "Run 'sigil pack' on Windows to emit the -Setup.exe.",
                            SourceLocation.Unknown,
                            "https://docs.sigil.build/diagnostics/SIG0270"),
                    }, useColor: false);
                    ctx.ExitCode = 1;
                    continue;
                }

                foreach (var arch in arches)
                {
                    // One artifact per (format, architecture). For the exe wrapper this
                    // yields one <App>-<ver>-<arch>-Setup.exe per declared architecture,
                    // mirroring the zip/msix loop; ExeWrapperPackager selects the staged
                    // per-RID runtime from options.Architecture.
                    IPackager packager = format switch
                    {
                        PackageFormat.Msix => new MsixPackager(),
                        PackageFormat.Zip => new ZipPackager(),
                        PackageFormat.Exe => new ExeWrapperPackager(),
                        _ => throw new System.NotSupportedException($"unknown format {format}"),
                    };

                    var sourceDir = System.IO.Path.IsPathRooted(manifest.Build.Source)
                        ? manifest.Build.Source
                        : System.IO.Path.GetFullPath(
                            System.IO.Path.Combine(
                                System.IO.Path.GetDirectoryName(path) ?? ".",
                                manifest.Build.Source));

                    var payloadMode = isWebPayload ? PayloadMode.Web : PayloadMode.Embedded;
                    var result = await packager.PackAsync(manifest,
                        new PackOptions(sourceDir, outDir, format, arch, payloadMode, packageUrl),
                        ctx.GetCancellationToken());

                    DiagnosticReporter.Write(Console.Error, result.Diagnostics, useColor: false);
                    if (result.Artifact is null) { ctx.ExitCode = 1; return; }
                    Console.Out.WriteLine($"  {result.Artifact.Path}  ({result.Artifact.SizeBytes} bytes, sha256 {result.Artifact.Sha256[..12]}…)");

                    // T12.5: `--payload web` emits a second artifact — the small stub
                    // whose only install action downloads + runs the artifact just
                    // printed above. Its absence (with web payload requested on the
                    // exe format) is itself a failure, not a silent no-op.
                    if (result.SecondaryArtifact is { } secondary)
                    {
                        Console.Out.WriteLine($"  {secondary.Path}  ({secondary.SizeBytes} bytes, sha256 {secondary.Sha256[..12]}…)");
                    }
                    else if (isWebPayload && format == PackageFormat.Exe)
                    {
                        ctx.ExitCode = 1;
                        return;
                    }
                }
            }

            // ctx.ExitCode defaults to 0; a non-Windows exe-format skip above sets it
            // to 1. Do not unconditionally reset here or that failure would be masked.
        });
        return cmd;
    }
}
