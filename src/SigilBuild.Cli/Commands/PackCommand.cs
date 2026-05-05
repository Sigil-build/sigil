using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using SigilBuild.Core.Configuration;
using SigilBuild.Core.Diagnostics;
using SigilBuild.Core.Manifest;
using SigilBuild.Packaging;
using SigilBuild.Packaging.Msix;
using SigilBuild.Packaging.Zip;

namespace SigilBuild.Cli.Commands;

public static class PackCommand
{
    public static Command Build()
    {
        var pathArg = new Argument<string>("path", () => "sigil.yaml", "Path to the manifest");
        var outOpt = new Option<string>("--out", () => "./dist", "Output directory");

        var cmd = new Command("pack", "Pack the app per the manifest");
        cmd.AddArgument(pathArg);
        cmd.AddOption(outOpt);
        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var path = ctx.ParseResult.GetValueForArgument(pathArg);
            var outDir = ctx.ParseResult.GetValueForOption(outOpt) ?? "./dist";

            var load = await ManifestLoader.LoadAsync(path, new ProcessEnvironmentReader());
            DiagnosticReporter.Write(Console.Error, load.Diagnostics, useColor: false);
            if (load.Manifest is null) { ctx.ExitCode = 1; return; }

            Directory.CreateDirectory(outDir);
            var manifest = load.Manifest;
            var formats = manifest.Package?.Formats ?? new[] { PackageFormat.Zip };
            var arches = manifest.Package?.Architectures ?? new[] { TargetArchitecture.X64 };

            foreach (var format in formats)
            foreach (var arch in arches)
            {
                IPackager packager = format == PackageFormat.Msix
                    ? (IPackager)new MsixPackager()
                    : new ZipPackager();

                var sourceDir = System.IO.Path.IsPathRooted(manifest.Build.Source)
                    ? manifest.Build.Source
                    : System.IO.Path.GetFullPath(
                        System.IO.Path.Combine(
                            System.IO.Path.GetDirectoryName(path) ?? ".",
                            manifest.Build.Source));

                var result = await packager.PackAsync(manifest,
                    new PackOptions(sourceDir, outDir, format, arch),
                    ctx.GetCancellationToken());

                DiagnosticReporter.Write(Console.Error, result.Diagnostics, useColor: false);
                if (result.Artifact is null) { ctx.ExitCode = 1; return; }
                Console.Out.WriteLine($"  {result.Artifact.Path}  ({result.Artifact.SizeBytes} bytes, sha256 {result.Artifact.Sha256[..12]}…)");
            }

            ctx.ExitCode = 0;
        });
        return cmd;
    }
}
