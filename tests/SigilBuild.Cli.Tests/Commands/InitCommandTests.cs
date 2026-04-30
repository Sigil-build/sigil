using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Cli;
using Xunit;

namespace SigilBuild.Cli.Tests.Commands;

public class InitCommandTests
{
    [Fact]
    public async Task Init_NonInteractive_WritesMinimalManifest()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var args = new[]
            {
                "init",
                "--non-interactive",
                "--out", Path.Combine(dir, "sigil.yaml"),
                "--template", "minimal",
                "--app-id", "com.example.GenApp",
                "--app-name", "GenApp",
                "--version", "0.1.0",
                "--publisher", "Acme",
            };
            var exit = await Program.MainAsync(args);
            exit.Should().Be(0);

            var written = await File.ReadAllTextAsync(Path.Combine(dir, "sigil.yaml"));
            written.Should().Contain("id: com.example.GenApp");
            written.Should().Contain("name: GenApp");
            written.Should().Contain("publisher: Acme");
            written.Should().NotContain("{APP_");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Init_FileExistsWithoutForce_ExitsOne()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "sigil.yaml");
        await File.WriteAllTextAsync(path, "# existing\n");
        try
        {
            var exit = await Program.MainAsync(new[]
            {
                "init", "--non-interactive", "--out", path,
                "--app-id", "x.y.Z", "--app-name", "Z", "--version", "0.1.0", "--publisher", "P",
            });
            exit.Should().Be(1);
            (await File.ReadAllTextAsync(path)).Should().Be("# existing\n");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
