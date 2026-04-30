using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Cli;
using Xunit;

namespace SigilBuild.Cli.Tests.Commands;

public class ValidateCommandTests
{
    [Fact]
    public async Task Validate_FormatJson_ProducesStructuredOutput()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".yaml");
        await File.WriteAllTextAsync(path,
            "spec: v1.0\napp: { id: bad, name: Example, version: 0.1.0, publisher: P }\nbuild: { source: ./out }\n");
        var sw = new StringWriter();
        var originalOut = System.Console.Out;
        System.Console.SetOut(sw);
        try
        {
            var exit = await Program.MainAsync(new[] { "validate", path, "--format", "json" });
            exit.Should().Be(1);
        }
        finally
        {
            System.Console.SetOut(originalOut);
            File.Delete(path);
        }

        var output = sw.ToString();
        var jsonStart = output.IndexOf('{');
        jsonStart.Should().BeGreaterOrEqualTo(0, "expected JSON in output but got: {0}", output);
        using var doc = JsonDocument.Parse(output.Substring(jsonStart));
        doc.RootElement.GetProperty("valid").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("path").GetString().Should().Be(path);
        doc.RootElement.GetProperty("diagnostics").EnumerateArray().Should().NotBeEmpty();
        var first = doc.RootElement.GetProperty("diagnostics")[0];
        first.GetProperty("code").GetString()!.Should().StartWith("SIG");
        first.GetProperty("severity").GetString().Should().Be("error");
    }

    [Fact]
    public async Task Validate_FormatJson_ValidFileEmitsValidTrue()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".yaml");
        await File.WriteAllTextAsync(path,
            "spec: v1.0\napp: { id: com.example.App, name: Example, version: 0.1.0, publisher: P }\nbuild: { source: ./out }\n");
        var sw = new StringWriter();
        var originalOut = System.Console.Out;
        System.Console.SetOut(sw);
        try
        {
            var exit = await Program.MainAsync(new[] { "validate", path, "--format", "json" });
            exit.Should().Be(0);
        }
        finally
        {
            System.Console.SetOut(originalOut);
            File.Delete(path);
        }

        using var doc = JsonDocument.Parse(sw.ToString());
        doc.RootElement.GetProperty("valid").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("diagnostics").GetArrayLength().Should().Be(0);
    }

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
