using System.IO;
using FluentAssertions;
using SigilBuild.Packaging.Msix;
using Xunit;

namespace SigilBuild.Packaging.Tests.Msix;

public class WindowsSdkLocatorTests
{
    [Fact]
    public void TryLocate_FromCustomRoot_FindsLatestVersion()
    {
        var root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var oldVer = Path.Combine(root, "10.0.22621.0", "x64");
        var newVer = Path.Combine(root, "10.0.26100.0", "x64");
        Directory.CreateDirectory(oldVer);
        Directory.CreateDirectory(newVer);
        File.WriteAllText(Path.Combine(oldVer, "MakeAppx.exe"), "");
        File.WriteAllText(Path.Combine(newVer, "MakeAppx.exe"), "");
        File.WriteAllText(Path.Combine(newVer, "makepri.exe"), "");
        try
        {
            var found = WindowsSdkLocator.TryLocateBinFromRoot(root, out var binDir);

            found.Should().BeTrue();
            binDir.Should().Be(newVer);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void TryLocate_NoSdkInRoot_ReturnsFalse()
    {
        var root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        try
        {
            var found = WindowsSdkLocator.TryLocateBinFromRoot(root, out _);
            found.Should().BeFalse();
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
