using System;
using System.Collections.Generic;
using System.IO;
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
/// End-to-end coverage for T5: an embedded payload archive is extracted, a
/// <c>file_copy</c> from a <c>payload://</c> source lands its file, and the
/// temp extraction dir is always cleaned up — on success and on rollback.
/// </summary>
public sealed class PayloadInstallTests
{
    [Fact]
    public async Task Install_copies_a_file_from_a_payload_source_then_cleans_temp()
    {
        using var dst = new TempDir();
        var container = PayloadExtractionTests.BuildPayload(("app/app.exe", "PAYLOAD-BYTES"));

        var extraction = PayloadExtraction.Extract(container, "com.acme.Studio");
        var root = extraction.Root;
        try
        {
            var ctx = new StepContext(new Dictionary<string, object?>(), extraction.Root);
            var steps = new InstallStep[]
            {
                new InstallStep.FileCopy("copy", "payload://app/app.exe", dst.Path,
                    Overwrite: true, When: null, OnFailure: OnFailure.Fail),
            };

            var result = await new InstallEngine().RunAsync(steps, ctx);

            result.Success.Should().BeTrue();
            var landed = Path.Combine(dst.Path, "app.exe");
            File.Exists(landed).Should().BeTrue("the payload:// source must resolve and copy");
            File.ReadAllText(landed).Should().Be("PAYLOAD-BYTES");
        }
        finally
        {
            // Mirrors InstallSession's finally: the payload temp dir is disposed
            // once the run completes.
            extraction.Dispose();
        }

        Directory.Exists(root).Should().BeFalse("the payload temp dir must be removed after install");
    }

    [Fact]
    public async Task InstallSession_extracts_resolves_and_cleans_temp_on_rollback()
    {
        using var dst = new TempDir();
        using var missing = new TempDir();
        var appId = "t5rollback" + Guid.NewGuid().ToString("N");

        var container = PayloadExtractionTests.BuildPayload(("app/app.exe", "PAYLOAD-BYTES"));

        var blob = new WrapperBlob(
            AppId: appId,
            Parameters: Array.Empty<ParameterDefinition>(),
            InstallSteps: new InstallStep[]
            {
                // 1) Copy from the extracted payload — journals a restore entry.
                new InstallStep.FileCopy("copy", "payload://app/app.exe", dst.Path,
                    Overwrite: true, When: null, OnFailure: OnFailure.Rollback),
                // 2) Fail deterministically (missing glob root) → engine rolls back.
                new InstallStep.FileCopy("boom",
                    Path.Combine(missing.Path, "no-such-dir", "*.txt"), dst.Path,
                    Overwrite: true, When: null, OnFailure: OnFailure.Rollback),
            },
            PreInstall: Array.Empty<InstallStep>(),
            PostInstall: Array.Empty<InstallStep>(),
            UpdateSteps: Array.Empty<InstallStep>());

        var parsed = CommandLineParser.Parse(new[] { "/silent" }, blob.Parameters);
        var session = InstallSession.ForTesting(blob, parsed);

        var outcome = await session.RunInstallCoreAsync(container, progress: null, CancellationToken.None);

        outcome.Success.Should().BeFalse("the second step fails with on_failure: rollback");

        // The payload-copied file was rolled back...
        File.Exists(Path.Combine(dst.Path, "app.exe")).Should().BeFalse("rollback must undo the payload copy");

        // ...and the extraction temp dir was cleaned by the finally, even on rollback.
        Directory.EnumerateDirectories(Path.GetTempPath(), $"sigil-{appId}-*")
            .Should().BeEmpty("the payload temp dir must be removed on the rollback path");
    }
}
