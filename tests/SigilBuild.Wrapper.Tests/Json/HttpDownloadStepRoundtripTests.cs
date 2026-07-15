using System.Text;
using System.Text.Json;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Json;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Json;

/// <summary>
/// P4: the http_download step survives the converter and the source-generated
/// (AOT-safe) JSON context in both directions.
/// </summary>
public class HttpDownloadStepRoundtripTests
{
    [Fact]
    public void HttpDownload_roundtrips_through_the_converter()
    {
        var step = new InstallStep.HttpDownload(
            "dl", "https://ex.com/a.zip", "{install_dir}/a.zip", "deadbeef",
            TimeoutSeconds: 90, Retries: 3, When: "param.x == true", OnFailure: OnFailure.Rollback);

        var wire = SerializableInstallStepConverter.FromInstallStep(step);
        wire.Type.Should().Be("http_download");

        var back = (InstallStep.HttpDownload)SerializableInstallStepConverter.ToInstallStep(wire);
        back.Should().BeEquivalentTo(step);
    }

    [Fact]
    public void HttpDownload_step_survives_the_full_blob_json_context()
    {
        var blob = new WrapperBlob(
            AppId: "com.acme.Studio",
            Parameters: System.Array.Empty<ParameterDefinition>(),
            InstallSteps: new InstallStep[]
            {
                new InstallStep.HttpDownload(
                    "dl", "https://ex.com/a.zip", "{install_dir}/a.zip", "abc123",
                    TimeoutSeconds: null, Retries: 2, When: null, OnFailure: OnFailure.Rollback),
            },
            PreInstall: System.Array.Empty<InstallStep>(),
            PostInstall: System.Array.Empty<InstallStep>(),
            UpdateSteps: System.Array.Empty<InstallStep>());

        var s = SerializableWrapperBlob.FromWrapperBlob(blob);
        var json = JsonSerializer.Serialize(s, WrapperBlobJsonContext.Default.SerializableWrapperBlob);
        var back = JsonSerializer.Deserialize(
            Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(json)),
            WrapperBlobJsonContext.Default.SerializableWrapperBlob);

        var step = (InstallStep.HttpDownload)SerializableWrapperBlob.ToWrapperBlob(back!).InstallSteps[0];
        step.Url.Should().Be("https://ex.com/a.zip");
        step.Dest.Should().Be("{install_dir}/a.zip");
        step.Sha256.Should().Be("abc123");
        step.Retries.Should().Be(2);
        step.TimeoutSeconds.Should().BeNull();
    }
}
