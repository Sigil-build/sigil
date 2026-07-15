using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Cli;
using SigilBuild.Wrapper.Engine;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Engine;

/// <summary>
/// P5 (gap G6): prerequisites wired through the <see cref="InstallSession"/> driver.
/// A prerequisite failure must abort the run BEFORE the journal opens (no partial
/// install) — verified here with a scope-required mismatch, which fails without
/// spawning any process.
/// </summary>
public sealed class PrerequisiteSessionTests
{
    private static WrapperBlob BlobWithPrereq(InstallerPrerequisite prereq, InstallScope scope = InstallScope.Auto) => new(
        AppId: "com.acme.Studio",
        Parameters: Array.Empty<ParameterDefinition>(),
        InstallSteps: Array.Empty<InstallStep>(),
        PreInstall: Array.Empty<InstallStep>(),
        PostInstall: Array.Empty<InstallStep>(),
        UpdateSteps: Array.Empty<InstallStep>(),
        Scope: scope,
        Prerequisites: new[] { prereq });

    [Fact]
    public async Task Prerequisite_scope_mismatch_aborts_before_the_journal()
    {
        // scope_required allusers, but an auto manifest with no flag resolves to user.
        var blob = BlobWithPrereq(new InstallerPrerequisite(
            "Machine Redist", "file_exists('c:/never')", "payload://x.exe",
            ScopeRequired: "allusers"));
        var parsed = CommandLineParser.Parse(new[] { "/silent" }, blob.Parameters);
        var session = InstallSession.ForTesting(blob, parsed);
        session.ResolvedScope.Should().Be(InstallScope.User);

        var outcome = await session.RunInstallAsync(progress: null, CancellationToken.None);

        outcome.Success.Should().BeFalse();
        outcome.Error.Should().Contain("Machine Redist");
        outcome.Error.Should().Contain("per-machine");
        session.RebootRequired.Should().BeFalse();
    }
}
