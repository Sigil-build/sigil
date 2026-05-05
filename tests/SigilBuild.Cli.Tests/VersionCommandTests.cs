using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Cli;
using Xunit;

namespace SigilBuild.Cli.Tests;

public class VersionCommandTests
{
    [Theory]
    [InlineData("--version")]
    [InlineData("-v")]
    [InlineData("version")]
    public async Task Main_WithVersionFlag_ReturnsZero(string arg)
    {
        var exitCode = await Program.MainAsync(new[] { arg });
        exitCode.Should().Be(0);
    }

    [Fact]
    public async Task Main_WithVersionFlag_PrintsAssemblyVersion()
    {
        using var sw = new System.IO.StringWriter();
        var originalOut = System.Console.Out;
        System.Console.SetOut(sw);
        try
        {
            await Program.MainAsync(new[] { "--version" });
        }
        finally
        {
            System.Console.SetOut(originalOut);
        }

        sw.ToString().Trim().Should().Be("0.0.1-alpha");
    }

    [Fact]
    public async Task Main_WithNoArgs_ReturnsNonZero()
    {
        var exitCode = await Program.MainAsync(System.Array.Empty<string>());
        exitCode.Should().NotBe(0);
    }
}
