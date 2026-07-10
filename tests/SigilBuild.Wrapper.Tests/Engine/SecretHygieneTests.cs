using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
/// Decision 6 / T9 secret hygiene: a <see cref="ParameterType.Secret"/> value must
/// never reach the on-screen/console log lines or the persisted uninstall state
/// (journal). These tests grep both surfaces for a known secret and assert it is
/// absent (redacted to <c>***</c>).
/// </summary>
public sealed class SecretHygieneTests
{
    private const string Secret = "LK-9999-TOPSECRET";

    private static ParameterDefinition LicenseKey() =>
        new("license_key", ParameterType.Secret, null, null, true, "License key", null, null, null);

    private static WrapperBlob BlobWith(params InstallStep[] steps) =>
        new(
            AppId: "com.acme.Studio",
            Parameters: new[] { LicenseKey() },
            InstallSteps: steps,
            PreInstall: Array.Empty<InstallStep>(),
            PostInstall: Array.Empty<InstallStep>(),
            UpdateSteps: Array.Empty<InstallStep>());

    [Fact]
    public void StepContext_collects_and_redacts_secret_values()
    {
        var blob = BlobWith();
        var parsed = CommandLineParser.Parse(new[] { $"/Plicense_key={Secret}" }, blob.Parameters);

        var ctx = StepContext.From(blob, parsed);

        ctx.SecretValues.Should().Contain(Secret);
        ctx.Redact($"activating with {Secret} now").Should().Be("activating with *** now");
    }

    [Fact]
    public async Task Secret_value_is_absent_from_journal_and_log_output()
    {
        using var tmp = new TempDir();
        // A step whose resolved path embeds the secret — so the rollback journal's
        // RemoveDirectory record would carry it verbatim if not redacted.
        var dir = tmp.Path + "/app-${parameters.license_key}";
        var blob = BlobWith(new InstallStep.DirectoryCreate("mk", dir, When: null, OnFailure.Fail));
        var parsed = CommandLineParser.Parse(new[] { $"/Plicense_key={Secret}" }, blob.Parameters);
        var ctx = StepContext.From(blob, parsed);

        var events = new List<StepProgress>();
        var progress = new ListProgress(events);

        var result = await new InstallEngine().RunAsync(
            Array.Empty<InstallStep>(), blob.InstallSteps, Array.Empty<InstallStep>(),
            ctx, progress, CancellationToken.None);

        result.Success.Should().BeTrue();
        Directory.Exists(Path.Combine(tmp.Path, $"app-{Secret}")).Should().BeTrue("the step should have run");

        // --- Grep the captured log output ---
        var log = string.Join("\n", events.Where(e => e.Message is not null).Select(e => e.Message));
        log.Should().NotContain(Secret, "no log line may leak a secret value");

        // --- Grep the persisted journal / uninstall state ---
        var appId = "t9-secret-" + Guid.NewGuid().ToString("N");
        try
        {
            UninstallStateStore.Save(appId, result.Journal, InstallScope.User, ctx.SecretValues);
            var content = File.ReadAllText(UninstallStateStore.PathFor(appId, InstallScope.User));
            content.Should().NotContain(Secret, "the persisted journal must never contain a secret value");
            content.Should().Contain("***", "the secret occurrence in the journal must be redacted");
        }
        finally
        {
            UninstallStateStore.Delete(appId, InstallScope.User);
        }
    }

    private sealed class ListProgress : IProgress<StepProgress>
    {
        private readonly List<StepProgress> _sink;
        public ListProgress(List<StepProgress> sink) => _sink = sink;
        public void Report(StepProgress value) => _sink.Add(value);
    }
}
