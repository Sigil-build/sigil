using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Cli;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Tests.Helpers;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Engine;

/// <summary>
/// P1 end-to-end (gap G1): a registry value read into an <c>installer.vars</c>
/// variable, referenced as a <c>{var.&lt;name&gt;}</c> brace token in a
/// <c>file_copy</c> destination, drives where the file actually lands when the
/// install engine runs — the declarative equivalent of NSIS
/// <c>ReadRegStr</c> + a path built from <c>$var</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class VarsIntegrationTests
{
    [Fact]
    public async Task Registry_value_flows_through_a_var_into_a_file_copy_destination()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var reg = TestRegistry.CreateScratchKey();
        using var tmp = new TempDir();

        var srcFile = Path.Combine(tmp.Path, "source.txt");
        File.WriteAllText(srcFile, "payload-bytes");

        // file_copy treats `to` as the destination DIRECTORY. Test setup writes
        // that directory into the registry; the manifest reads it back at session
        // start via registry_read into var.dest, and the step's destination is the
        // {var.dest} brace token.
        var destDir = Path.Combine(tmp.Path, "installed", "acme");
        reg.SetValue("Dest", destDir);

        var blob = new WrapperBlob(
            AppId: "com.acme.VarTest",
            Parameters: Array.Empty<ParameterDefinition>(),
            InstallSteps: new InstallStep[]
            {
                new InstallStep.FileCopy("cp", srcFile, "{var.dest}", Overwrite: true, When: null, OnFailure.Fail),
            },
            PreInstall: Array.Empty<InstallStep>(),
            PostInstall: Array.Empty<InstallStep>(),
            UpdateSteps: Array.Empty<InstallStep>(),
            Vars: new[]
            {
                new InstallerVar("dest", $"registry_read('HKCU', '{reg.Path}', 'Dest')"),
            });

        var parsed = CommandLineParser.Parse(Array.Empty<string>(), blob.Parameters);
        var ctx = StepContext.From(blob, parsed);

        // The {var.dest} token resolves to the registry value.
        ctx.ResolvePath("{var.dest}").Should().Be(destDir);

        var result = await new InstallEngine().RunAsync(
            Array.Empty<InstallStep>(), blob.InstallSteps, Array.Empty<InstallStep>(),
            ctx, new NullProgress(), CancellationToken.None);

        result.Success.Should().BeTrue();
        var landed = Path.Combine(destDir, "source.txt");
        File.Exists(landed).Should().BeTrue("the file_copy destination came from a registry-backed var");
        File.ReadAllText(landed).Should().Be("payload-bytes");
    }

    private sealed class NullProgress : IProgress<StepProgress>
    {
        public void Report(StepProgress value) { }
    }
}
