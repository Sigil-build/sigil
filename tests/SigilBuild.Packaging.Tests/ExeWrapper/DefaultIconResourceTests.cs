using System.IO;
using FluentAssertions;
using Xunit;

namespace SigilBuild.Packaging.Tests.ExeWrapper;

public class DefaultIconResourceTests
{
    [Fact]
    public void DefaultIcon_IsEmbeddedAndStartsWithIconMagicBytes()
    {
        var asm = typeof(SigilBuild.Packaging.ExeWrapper.WrapperResourceWriter).Assembly;
        using var s = asm.GetManifestResourceStream("SigilBuild.Packaging.DefaultInstallerIcon.ico");
        s.Should().NotBeNull("the default installer icon must be embedded in the Packaging assembly");
        var header = new byte[6];
        s!.ReadExactly(header);
        header[0].Should().Be(0);
        header[1].Should().Be(0);
        header[2].Should().Be(1);
        header[3].Should().Be(0);
    }
}
