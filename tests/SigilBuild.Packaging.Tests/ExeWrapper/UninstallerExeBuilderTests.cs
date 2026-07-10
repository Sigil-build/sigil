using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Packaging.ExeWrapper;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Json;
using Xunit;

namespace SigilBuild.Packaging.Tests.ExeWrapper;

public class UninstallerExeBuilderTests
{
    [Fact]
    public async Task Build_ProducesAStampedWrapperExeWithIsUninstallerTrue()
    {
        string stubExe;
        try { stubExe = WrapperRuntimeLocator.Locate(); }
        catch (FileNotFoundException) { return; /* soft-skip — AOT runtime not staged */ }

        var uninstallSteps = new InstallStep[]
        {
            new InstallStep.RunProgram(
                Id: "noop",
                Program: "cmd.exe",
                Args: new[] { "/c", "echo", "hello" },
                Wait: true,
                Cwd: null,
                ExpectedExitCodes: null,
                TimeoutSeconds: 30,
                When: null,
                OnFailure: OnFailure.Continue),
        };
        var app = new AppMetadata("com.example", "Example", "1.0.0", "ExampleCo", null, null);

        var produced = await UninstallerExeBuilder.BuildAsync(
            stubExe, appId: "com.example", app: app, uninstallSteps, CancellationToken.None);

        try
        {
            File.Exists(produced).Should().BeTrue();
            new FileInfo(produced).Length.Should().BeGreaterThan(1_000_000,
                "the uninstaller is a stamped copy of the AOT-published wrapper (~3.7 MB)");

            var blobBytes = ResourceReader.Read(produced, "SIGIL_BLOB_V1");
            var json = System.Text.Encoding.UTF8.GetString(blobBytes);
            var deserialized = System.Text.Json.JsonSerializer.Deserialize(
                json, WrapperBlobJsonContext.Default.SerializableWrapperBlob);
            deserialized.Should().NotBeNull();
            deserialized!.IsUninstaller.Should().BeTrue();
            deserialized.InstallSteps.Should().HaveCount(1, "uninstall steps are wired into InstallSteps");
        }
        finally
        {
            try { File.Delete(produced); } catch { /* best-effort */ }
        }
    }
}
