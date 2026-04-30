using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Cli;
using Xunit;

namespace SigilBuild.Cli.Tests.Commands;

public class ValidateCommandTests
{
    [Fact]
    public async Task Validate_ValidFile_ExitsZero()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".yaml");
        await File.WriteAllTextAsync(path, """
            spec: v1.0
            app: { id: com.example.App, name: Example, version: 0.1.0, publisher: Example Inc. }
            build: { source: ./out }
            """);
        try
        {
            var exit = await Program.MainAsync(new[] { "validate", path });
            exit.Should().Be(0);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Validate_MissingFile_ExitsOne()
    {
        var exit = await Program.MainAsync(new[] { "validate", "/no/such/file.yaml" });
        exit.Should().Be(1);
    }

    [Fact]
    public async Task Validate_InvalidVersion_ExitsOne()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".yaml");
        await File.WriteAllTextAsync(path,
            "spec: v1.0\napp: { id: com.example.X, name: X, version: not-a-semver, publisher: P }\nbuild: { source: ./out }\n");
        try
        {
            var exit = await Program.MainAsync(new[] { "validate", path });
            exit.Should().Be(1);
        }
        finally { File.Delete(path); }
    }
}
