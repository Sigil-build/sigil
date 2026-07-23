using System.Text;
using System.Text.Json;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Json;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Json;

/// <summary>
/// T11.3 (P11): the firewall_rule step survives the
/// <see cref="SerializableInstallStepConverter"/> and the full source-generated
/// (AOT-safe) <see cref="WrapperBlobJsonContext"/> blob round-trip in both
/// directions — mirroring <c>ComRegisterStepRoundtripTests</c>.
/// </summary>
public class FirewallRuleStepRoundtripTests
{
    private static readonly InstallStep.FirewallRule Full = new(
        Id: "open_firewall_port",
        Name: "AcmeAppInbound",
        Direction: "in",
        Action: "allow",
        Program: "{install_dir}/AcmeApp.exe",
        Port: 8443,
        Protocol: "tcp",
        When: "param.enable_networking == true",
        OnFailure: OnFailure.Rollback);

    [Fact]
    public void Roundtrips_through_the_converter()
    {
        var wire = SerializableInstallStepConverter.FromInstallStep(Full);
        wire.Type.Should().Be("firewall_rule");

        var back = (InstallStep.FirewallRule)SerializableInstallStepConverter.ToInstallStep(wire);
        back.Should().BeEquivalentTo(Full);
    }

    [Fact]
    public void Optional_program_port_and_protocol_survive_as_null()
    {
        var step = new InstallStep.FirewallRule(
            "fw", "AcmeApp", Direction: "out", Action: "block",
            Program: null, Port: null, Protocol: null,
            When: null, OnFailure: OnFailure.Fail);

        var back = (InstallStep.FirewallRule)SerializableInstallStepConverter.ToInstallStep(
            SerializableInstallStepConverter.FromInstallStep(step));

        back.Should().BeEquivalentTo(step);
    }

    [Fact]
    public void Survives_the_full_blob_json_context()
    {
        var blob = new WrapperBlob(
            AppId: "com.acme.Studio",
            Parameters: System.Array.Empty<ParameterDefinition>(),
            InstallSteps: new InstallStep[] { Full },
            PreInstall: System.Array.Empty<InstallStep>(),
            PostInstall: System.Array.Empty<InstallStep>(),
            UpdateSteps: System.Array.Empty<InstallStep>());

        var s = SerializableWrapperBlob.FromWrapperBlob(blob);
        var json = JsonSerializer.Serialize(s, WrapperBlobJsonContext.Default.SerializableWrapperBlob);
        var back = JsonSerializer.Deserialize(
            Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(json)),
            WrapperBlobJsonContext.Default.SerializableWrapperBlob);

        var step = (InstallStep.FirewallRule)SerializableWrapperBlob.ToWrapperBlob(back!).InstallSteps[0];
        step.Should().BeEquivalentTo(Full);
    }
}
