using System.Text;
using System.Text.Json;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Json;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Json;

/// <summary>
/// T11.1 (P11): the scheduled_task_create step survives the
/// <see cref="SerializableInstallStepConverter"/> and the full source-generated
/// (AOT-safe) <see cref="WrapperBlobJsonContext"/> blob round-trip in both
/// directions — mirroring <c>HttpDownloadStepRoundtripTests</c>.
/// </summary>
public class ScheduledTaskCreateStepRoundtripTests
{
    private static readonly InstallStep.ScheduledTaskCreate Full = new(
        Id: "install_updater_task",
        Name: "AcmeUpdaterTask",
        Program: "{install_dir}/updater.exe",
        Arguments: "--check --silent",
        Trigger: "daily",
        RunLevel: "highest",
        When: "param.autostart == true",
        OnFailure: OnFailure.Rollback);

    [Fact]
    public void Roundtrips_through_the_converter()
    {
        var wire = SerializableInstallStepConverter.FromInstallStep(Full);
        wire.Type.Should().Be("scheduled_task_create");

        var back = (InstallStep.ScheduledTaskCreate)SerializableInstallStepConverter.ToInstallStep(wire);
        back.Should().BeEquivalentTo(Full);
    }

    [Fact]
    public void Optional_arguments_and_when_survive_as_null()
    {
        var step = new InstallStep.ScheduledTaskCreate(
            "t", "MyTask", "app.exe", Arguments: null, Trigger: "onstart", RunLevel: "limited",
            When: null, OnFailure: OnFailure.Fail);

        var back = (InstallStep.ScheduledTaskCreate)SerializableInstallStepConverter.ToInstallStep(
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

        var step = (InstallStep.ScheduledTaskCreate)SerializableWrapperBlob.ToWrapperBlob(back!).InstallSteps[0];
        step.Should().BeEquivalentTo(Full);
    }
}
