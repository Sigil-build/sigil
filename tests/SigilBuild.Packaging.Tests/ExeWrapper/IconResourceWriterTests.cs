using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Packaging.ExeWrapper;
using Xunit;

namespace SigilBuild.Packaging.Tests.ExeWrapper;

public class IconResourceWriterTests
{
    [Fact(Skip = "Requires the AOT-published Wrapper.exe in runtimes/win-x64/.")]
    public async Task WriteAsync_ReplacesIconInWrapperExe()
    {
        var stubExe = WrapperRuntimeLocator.Locate();
        var tmp = Path.Combine(Path.GetTempPath(), $"sigil-icon-{Guid.NewGuid():N}.exe");
        File.Copy(stubExe, tmp, overwrite: true);
        try
        {
            var asm = typeof(WrapperResourceWriter).Assembly;
            await using var iconStream = asm.GetManifestResourceStream("SigilBuild.Packaging.DefaultInstallerIcon.ico")!;
            using var ms = new MemoryStream();
            await iconStream.CopyToAsync(ms);

            await IconResourceWriter.WriteAsync(tmp, ms.ToArray(), CancellationToken.None);

            var resourceBytes = ResourceReader.ReadIconGroup(tmp, "MAINICON");
            resourceBytes.Length.Should().BeGreaterThan(6, "RT_GROUP_ICON header is at least 6 bytes (ICONDIR)");
        }
        finally
        {
            try { File.Delete(tmp); } catch (IOException) { }
        }
    }
}
