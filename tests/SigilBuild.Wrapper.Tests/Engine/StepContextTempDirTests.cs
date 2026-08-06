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
    public void ResolvePath_refuses_an_unknown_brace_token_alongside_temp_dir()
    {
        // Was `ResolvePath_leaves_unknown_brace_tokens_untouched_around_temp_dir`,
        // which asserted precisely the behaviour register row R16 identifies as the
        // bug: an unknown token was left LITERAL, so this path became a real
        // directory named "{not_a_real_token}" under the temp root and the install
        // reported success. The half of the original assertion that still holds —
        // {temp_dir} itself substitutes — is preserved below: the message quotes the
        // resolved path, and the only token left in it is the unknown one.
        var act = () => StepContext.Empty.ResolvePath("{temp_dir}/{not_a_real_token}");

        var expectedRoot = Path.GetTempPath()
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var message = act.Should().Throw<System.FormatException>()
            .WithMessage("*unresolved token '{not_a_real_token}'*")
            .Which.Message;

        // The message quotes the resolved path first and the original template
        // second, so {temp_dir} does appear — in the "(from '…')" half. What
        // matters is that the RESOLVED half shows the expanded temp root.
        message.Should().Contain(
            expectedRoot + "/{not_a_real_token}",
            "the known token still substitutes; only the unknown one survives");
    }
}
