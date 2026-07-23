using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Cli;
using Xunit;

namespace SigilBuild.Cli.Tests.Commands;

[Collection("CliCommands")]
public class PackCommandTests
{
    [Fact]
    public async Task Pack_ExeFormat_NoLongerRejectsAtDispatch_AndReportsSig0120WhenRuntimeMissing()
    {
        var workDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName());
        var srcDir = System.IO.Path.Combine(workDir, "out");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(System.IO.Path.Combine(srcDir, "app.txt"), "hello");
        var manifestPath = System.IO.Path.Combine(workDir, "sigil.yaml");
        File.WriteAllText(manifestPath, """
            spec: v1.0
            app: { id: com.example.App, name: Example, version: 1.0.0, publisher: Example Inc. }
            build: { source: ./out }
            package: { formats: [exe], architectures: [x64] }
            """);
        var outDir = System.IO.Path.Combine(workDir, "dist");
        try
        {
            // Capture stderr because the diagnostic is reported there.
            var origErr = System.Console.Error;
            using var capturedErr = new StringWriter();
            System.Console.SetError(capturedErr);
            try
            {
                // Pre-Task 14, this would throw NotSupportedException at the dispatch switch.
                // Post-Task 14, the packager runs; when the AOT runtime isn't staged in the
                // test process's runtimes/win-x64/ folder, ExeWrapperPackager surfaces SIG0120
                // and the CLI exits 1. When the runtime IS staged (CI happy path), exit 0.
                var exit = await Program.MainAsync(new[] { "pack", manifestPath, "--out", outDir });
                exit.Should().BeOneOf(0, 1);
                if (exit == 1)
                {
                    capturedErr.ToString().Should().Contain("SIG0120");
                }
            }
            finally
            {
                System.Console.SetError(origErr);
            }
        }
        finally { Directory.Delete(workDir, recursive: true); }
    }

    /// <summary>
    /// P12 / T12.5: `--payload web` requires a resolvable HTTPS `--package-url`.
    /// Missing → SIG0322, pack refuses before even loading the manifest.
    /// </summary>
    [Fact]
    public async Task Pack_PayloadWeb_WithoutPackageUrl_ReportsSig0322AndExits1()
    {
        var workDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var srcDir = Path.Combine(workDir, "out");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, "app.txt"), "hello");
        var manifestPath = Path.Combine(workDir, "sigil.yaml");
        File.WriteAllText(manifestPath, """
            spec: v1.0
            app: { id: com.example.App, name: Example, version: 1.0.0, publisher: Example Inc. }
            build: { source: ./out }
            package: { formats: [exe], architectures: [x64] }
            """);
        try
        {
            var origErr = System.Console.Error;
            using var capturedErr = new StringWriter();
            System.Console.SetError(capturedErr);
            try
            {
                var exit = await Program.MainAsync(new[] { "pack", manifestPath, "--payload", "web" });
                exit.Should().Be(1);
                capturedErr.ToString().Should().Contain("SIG0322");
            }
            finally
            {
                System.Console.SetError(origErr);
            }
        }
        finally { Directory.Delete(workDir, recursive: true); }
    }

    /// <summary>
    /// P12 / T12.5: a non-https `--package-url` is refused the same as a missing
    /// one — SIG0322, HTTPS-only.
    /// </summary>
    [Fact]
    public async Task Pack_PayloadWeb_WithNonHttpsPackageUrl_ReportsSig0322AndExits1()
    {
        var workDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var srcDir = Path.Combine(workDir, "out");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, "app.txt"), "hello");
        var manifestPath = Path.Combine(workDir, "sigil.yaml");
        File.WriteAllText(manifestPath, """
            spec: v1.0
            app: { id: com.example.App, name: Example, version: 1.0.0, publisher: Example Inc. }
            build: { source: ./out }
            package: { formats: [exe], architectures: [x64] }
            """);
        try
        {
            var origErr = System.Console.Error;
            using var capturedErr = new StringWriter();
            System.Console.SetError(capturedErr);
            try
            {
                var exit = await Program.MainAsync(new[]
                {
                    "pack", manifestPath, "--payload", "web", "--package-url", "http://cdn.example.com/pkg.exe",
                });
                exit.Should().Be(1);
                capturedErr.ToString().Should().Contain("SIG0322");
            }
            finally
            {
                System.Console.SetError(origErr);
            }
        }
        finally { Directory.Delete(workDir, recursive: true); }
    }

    /// <summary>
    /// P12 / T12.5: a valid `--payload web --package-url https://...` invocation
    /// passes SIG0322 validation and reaches the packager. Mirrors the existing
    /// exe-format gating tests — when the AOT host runtime isn't staged in this
    /// test process, the packager surfaces SIG0120 (missing runtime) instead of
    /// the SIG0322 usage error; either way SIG0322 must NOT fire for a valid URL.
    /// </summary>
    [Fact]
    public async Task Pack_PayloadWeb_WithValidHttpsPackageUrl_DoesNotReportSig0322()
    {
        var workDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var srcDir = Path.Combine(workDir, "out");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, "app.txt"), "hello");
        var manifestPath = Path.Combine(workDir, "sigil.yaml");
        File.WriteAllText(manifestPath, """
            spec: v1.0
            app: { id: com.example.App, name: Example, version: 1.0.0, publisher: Example Inc. }
            build: { source: ./out }
            package: { formats: [exe], architectures: [x64] }
            """);
        var outDir = Path.Combine(workDir, "dist");
        try
        {
            var origErr = System.Console.Error;
            using var capturedErr = new StringWriter();
            System.Console.SetError(capturedErr);
            try
            {
                var exit = await Program.MainAsync(new[]
                {
                    "pack", manifestPath, "--out", outDir,
                    "--payload", "web", "--package-url", "https://cdn.example.com/pkg.exe",
                });
                exit.Should().BeOneOf(0, 1);
                capturedErr.ToString().Should().NotContain("SIG0322");
            }
            finally
            {
                System.Console.SetError(origErr);
            }
        }
        finally { Directory.Delete(workDir, recursive: true); }
    }

    /// <summary>
    /// `--payload embedded` (the default, and passing it explicitly) must not
    /// change the zip-format pack path at all.
    /// </summary>
    [Fact]
    public async Task Pack_PayloadEmbedded_Explicit_ZipFormat_Unchanged()
    {
        var workDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var srcDir = Path.Combine(workDir, "out");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, "app.txt"), "hello");
        var manifestPath = Path.Combine(workDir, "sigil.yaml");
        File.WriteAllText(manifestPath, """
            spec: v1.0
            app: { id: com.example.App, name: Example, version: 1.0.0, publisher: Example Inc. }
            build: { source: ./out }
            package: { formats: [zip], architectures: [x64] }
            """);
        var outDir = Path.Combine(workDir, "dist");
        try
        {
            var exit = await Program.MainAsync(
                new[] { "pack", manifestPath, "--out", outDir, "--payload", "embedded" });
            exit.Should().Be(0);

            var zips = Directory.GetFiles(outDir, "*.zip");
            zips.Should().ContainSingle();
        }
        finally { Directory.Delete(workDir, recursive: true); }
    }

    [Fact]
    public async Task Pack_ZipFormat_ProducesArtifactInOutputDirectory()
    {
        var workDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName());
        var srcDir = System.IO.Path.Combine(workDir, "out");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(System.IO.Path.Combine(srcDir, "app.txt"), "hello");
        var manifestPath = System.IO.Path.Combine(workDir, "sigil.yaml");
        File.WriteAllText(manifestPath, """
            spec: v1.0
            app: { id: com.example.App, name: Example, version: 1.0.0, publisher: Example Inc. }
            build: { source: ./out }
            package: { formats: [zip], architectures: [x64] }
            """);
        var outDir = System.IO.Path.Combine(workDir, "dist");
        try
        {
            var exit = await Program.MainAsync(new[] { "pack", manifestPath, "--out", outDir });
            exit.Should().Be(0);

            var zips = Directory.GetFiles(outDir, "*.zip");
            zips.Should().ContainSingle();
            using var zip = ZipFile.OpenRead(zips[0]);
            zip.Entries.Should().Contain(e => e.FullName == "app.txt");
        }
        finally { Directory.Delete(workDir, recursive: true); }
    }
}
