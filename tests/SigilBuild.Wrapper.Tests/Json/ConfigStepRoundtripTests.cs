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

    [Theory]
    [InlineData(JsonValueType.Text)]
    [InlineData(JsonValueType.Json)]
    public void JsonEdit_value_type_roundtrips(JsonValueType valueType)
    {
        var step = new InstallStep.JsonEdit(
            "j", "a.json", "/a", "42", CreateIfMissing: false, null, OnFailure.Fail, valueType);

        RoundTrip(step).Should().BeEquivalentTo(step);
    }

    /// <summary>
    /// Register row R35. A blob written before <c>JsonEditValueType</c> existed carries
    /// no such property, and it must decode to the SAFE mode rather than to the
    /// inferring one — otherwise the fix would apply only to freshly packed installers.
    /// </summary>
    [Fact]
    public void JsonEdit_from_a_blob_with_no_value_type_decodes_as_string()
    {
        var legacy = new SerializableInstallStep
        {
            Id = "j",
            Type = "json_edit",
            OnFailure = "fail",
            Path = "a.json",
            Pointer = "/a",
            JsonEditValue = "true",
            // JsonEditValueType deliberately absent.
        };

        SerializableInstallStepConverter.ToInstallStep(legacy)
            .Should().BeOfType<InstallStep.JsonEdit>()
            .Which.ValueType.Should().Be(JsonValueType.Text);
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
