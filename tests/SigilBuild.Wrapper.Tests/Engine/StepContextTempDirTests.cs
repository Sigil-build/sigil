namespace SigilBuild.Wrapper.Tests.Engine;

using System.IO;
using FluentAssertions;
using SigilBuild.Wrapper.Engine;
using Xunit;

/// <summary>
/// P12 (T12.5): the <c>{temp_dir}</c> brace token the web-installer stub's
/// synthesized <c>http_download</c> step uses for its <c>dest</c> — a temp
/// location resolvable at INSTALL time so the packed step string itself stays a
/// deterministic literal (no GUID/timestamp baked in at pack time).
/// </summary>
public class StepContextTempDirTests
{
    [Fact]
    public void ResolvePath_expands_temp_dir_token_to_the_process_temp_directory()
    {
        var expectedRoot = Path.GetTempPath()
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var resolved = StepContext.Empty.ResolvePath("{temp_dir}/Acme-1.0.0-x64-Setup.exe");

        resolved.Should().Be(expectedRoot + "/Acme-1.0.0-x64-Setup.exe");
    }

    [Fact]
    public void ResolvePath_leaves_unknown_brace_tokens_untouched_around_temp_dir()
    {
        var resolved = StepContext.Empty.ResolvePath("{temp_dir}/{not_a_real_token}");

        resolved.Should().NotContain("{temp_dir}");
        resolved.Should().Contain("{not_a_real_token}", "unknown tokens are left literal, unaffected by the new substitution");
    }
}
