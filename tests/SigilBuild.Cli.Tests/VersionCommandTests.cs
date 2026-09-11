using System.Reflection;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Cli;
using Xunit;

namespace SigilBuild.Cli.Tests;

[Collection("CliCommands")]
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
    public async Task Reported_version_matches_the_assembly_informational_version()
    {
        // The CLI must report the version the build stamped onto the
        // assembly, not a hand-maintained const -- assert agreement with
        // AssemblyInformationalVersionAttribute rather than a literal. (R24)
        var expected = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion
            .Split('+')[0];          // strip any source-revision suffix

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

        sw.ToString().Trim().Should().Be(expected,
            "the CLI must report the version the build stamped, not a hand-maintained const");
    }

    [Fact]
    public async Task Main_WithNoArgs_ReturnsNonZero()
    {
        var exitCode = await Program.MainAsync(System.Array.Empty<string>());
        exitCode.Should().NotBe(0);
    }
}
