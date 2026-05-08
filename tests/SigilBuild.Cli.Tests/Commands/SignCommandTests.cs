using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Cli;
using Xunit;

namespace SigilBuild.Cli.Tests.Commands;

public sealed class SignCommandTests
{
    [Fact]
    public async Task Sign_ProviderNone_ExitsZeroAndDoesNothing()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        var artifact = Path.Combine(dir, "app.zip");
        File.WriteAllBytes(artifact, new byte[] { 0x50, 0x4B, 0x03, 0x04 });

        var manifestPath = Path.Combine(dir, "sigil.yaml");
        File.WriteAllText(manifestPath, """
            spec: v1.0
            app: { id: com.example.App, name: Example, version: 0.1.0, publisher: Example Inc. }
            build: { source: ./out }
            sign: { provider: none }
            """);

        try
        {
            var exit = await Program.MainAsync(new[] { "sign", manifestPath, "--artifact", artifact });
            exit.Should().Be(0);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
