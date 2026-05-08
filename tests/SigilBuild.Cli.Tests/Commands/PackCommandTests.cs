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
