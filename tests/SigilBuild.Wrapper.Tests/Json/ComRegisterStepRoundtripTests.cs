using System.Text;
using System.Text.Json;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Json;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Json;

/// <summary>
/// T11.2 (P11): the com_register step survives the
/// <see cref="SerializableInstallStepConverter"/> and the full source-generated
/// (AOT-safe) <see cref="WrapperBlobJsonContext"/> blob round-trip in both
/// directions — mirroring <c>ScheduledTaskCreateStepRoundtripTests</c>.
/// </summary>
public class ComRegisterStepRoundtripTests
{
    private static readonly InstallStep.ComRegister Full = new(
        Id: "register_shell_ext",
        Path: "{install_dir}/Acme.Shell.dll",
        When: "param.shell_integration == true",
        OnFailure: OnFailure.Rollback);

    [Fact]
    public void Roundtrips_through_the_converter()
    {
        var wire = SerializableInstallStepConverter.FromInstallStep(Full);
        wire.Type.Should().Be("com_register");
        wire.Path.Should().Be("{install_dir}/Acme.Shell.dll");

        var back = (InstallStep.ComRegister)SerializableInstallStepConverter.ToInstallStep(wire);
        back.Should().BeEquivalentTo(Full);
    }

    [Fact]
    public void Optional_when_survives_as_null()
    {
        var step = new InstallStep.ComRegister(
            "r", "codec.dll", When: null, OnFailure: OnFailure.Fail);

        var back = (InstallStep.ComRegister)SerializableInstallStepConverter.ToInstallStep(
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

        var step = (InstallStep.ComRegister)SerializableWrapperBlob.ToWrapperBlob(back!).InstallSteps[0];
        step.Should().BeEquivalentTo(Full);
    }
}
