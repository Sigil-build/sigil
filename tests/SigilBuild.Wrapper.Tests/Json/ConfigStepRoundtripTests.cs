using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Json;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Json;

/// <summary>
/// P8: the three config-edit steps survive the SerializableInstallStep converter
/// (AOT-safe wire form) in both directions.
/// </summary>
public class ConfigStepRoundtripTests
{
    private static InstallStep RoundTrip(InstallStep step) =>
        SerializableInstallStepConverter.ToInstallStep(
            SerializableInstallStepConverter.FromInstallStep(step));

    [Fact]
    public void IniWrite_roundtrips()
    {
        var step = new InstallStep.IniWrite("i", "a.ini", "app", "k", "v", CreateIfMissing: true, "param.x == true", OnFailure.Rollback);
        RoundTrip(step).Should().BeEquivalentTo(step);
    }

    [Fact]
    public void JsonEdit_roundtrips()
    {
        var step = new InstallStep.JsonEdit("j", "a.json", "/a/b/c", "42", CreateIfMissing: false, null, OnFailure.Continue);
        RoundTrip(step).Should().BeEquivalentTo(step);
    }

    [Fact]
    public void XmlEdit_roundtrips_with_and_without_attribute()
    {
        var withAttr = new InstallStep.XmlEdit("x", "a.xml", "/root/a", "id", "5", CreateIfMissing: true, null, OnFailure.Fail);
        RoundTrip(withAttr).Should().BeEquivalentTo(withAttr);

        var noAttr = new InstallStep.XmlEdit("x2", "a.xml", "/root/b", null, "text", CreateIfMissing: false, null, OnFailure.Fail);
        RoundTrip(noAttr).Should().BeEquivalentTo(noAttr);
    }
}
