using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Tests.Helpers;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Engine;

/// <summary>
/// Coverage for <see cref="StepContext.ResolvePath"/> — the <c>payload://</c>
/// rebasing that path-taking steps use to point at the extracted payload root.
/// </summary>
public sealed class StepContextPayloadTests
{
    private static StepContext WithRoot(string root) =>
        new(new Dictionary<string, object?>(), root);

    [Fact]
    public void ResolvePath_rebases_payload_scheme_onto_the_root()
    {
        using var root = new TempDir();
        var ctx = WithRoot(root.Path);

        var resolved = ctx.ResolvePath("payload://app/app.exe");

        resolved.Should().Be(Path.GetFullPath(Path.Combine(root.Path, "app", "app.exe")));
    }

    [Fact]
    public void ResolvePath_expands_templates_before_rebasing()
    {
        using var root = new TempDir();
        var ctx = new StepContext(
            new Dictionary<string, object?> { ["parameters.sub"] = "bin" },
            root.Path);

        var resolved = ctx.ResolvePath("payload://${parameters.sub}/tool.exe");

        resolved.Should().Be(Path.GetFullPath(Path.Combine(root.Path, "bin", "tool.exe")));
    }

    [Fact]
    public void ResolvePath_passes_through_non_payload_paths()
    {
        using var root = new TempDir();
        var ctx = WithRoot(root.Path);

        var absolute = Path.Combine("C:", "Program Files", "App", "app.exe");
        ctx.ResolvePath(absolute).Should().Be(absolute);
    }

    [Fact]
    public void ResolvePath_throws_when_no_payload_is_available()
    {
        var ctx = StepContext.Empty; // PayloadRoot is null.

        var act = () => ctx.ResolvePath("payload://app/app.exe");

        act.Should().Throw<System.FormatException>()
            .WithMessage("*no payload*");
    }

    [Fact]
    public void ResolvePath_rejects_traversal_out_of_the_payload_root()
    {
        using var root = new TempDir();
        var ctx = WithRoot(root.Path);

        var act = () => ctx.ResolvePath("payload://../secrets.txt");

        act.Should().Throw<System.FormatException>()
            .WithMessage("*escapes the payload root*");
    }
}
