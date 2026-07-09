using FluentAssertions;
using SigilBuild.Wrapper.Engine;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Engine;

/// <summary>
/// T12 self-elevation helper: argument forwarding (exact command-line quoting for
/// the <c>runas</c> relaunch) and the elevation probe. The actual UAC relaunch is
/// gated to the VM job — a unit test cannot elevate.
/// </summary>
public sealed class ElevationTests
{
    [Fact]
    public void BuildCommandLine_forwards_simple_args_verbatim()
    {
        Elevation.BuildCommandLine(new[] { "/silent", "/allusers", "/Uninstall" })
            .Should().Be("/silent /allusers /Uninstall");
    }

    [Fact]
    public void BuildCommandLine_quotes_args_that_contain_spaces()
    {
        // The /D=path override with a spaced install dir must survive the relaunch.
        Elevation.BuildCommandLine(new[] { "/silent", @"/D=C:\Program Files\Acme" })
            .Should().Be("/silent \"/D=C:\\Program Files\\Acme\"");
    }

    [Fact]
    public void BuildCommandLine_escapes_embedded_quotes()
    {
        // An embedded double-quote is backslash-escaped inside the quoted arg.
        Elevation.BuildCommandLine(new[] { "a\"b c" })
            .Should().Be("\"a\\\"b c\"");
    }

    [Fact]
    public void BuildCommandLine_doubles_a_trailing_backslash_inside_a_quoted_arg()
    {
        // A trailing backslash in a spaced path must be doubled so it does not
        // escape the closing quote (CommandLineToArgvW rules).
        Elevation.BuildCommandLine(new[] { @"C:\Program Files\" })
            .Should().Be("\"C:\\Program Files\\\\\"");
    }

    [Fact]
    public void BuildCommandLine_of_empty_list_is_empty()
    {
        Elevation.BuildCommandLine(System.Array.Empty<string>()).Should().BeEmpty();
    }

    [Fact]
    public void IsProcessElevated_returns_without_throwing()
    {
        // Value depends on how the test host is launched; we only assert the probe
        // is callable and total (never throws) — the relaunch decision builds on it.
        var act = () => Elevation.IsProcessElevated();
        act.Should().NotThrow();
    }
}
