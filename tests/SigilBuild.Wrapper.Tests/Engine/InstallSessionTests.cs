using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Wrapper.Cli;
using SigilBuild.Wrapper.Engine;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Engine;

/// <summary>
/// Exit-code and mode-routing plumbing for the shared install driver. These run
/// against an un-stamped runtime (no <c>SIGIL_BLOB_V1</c> resource), so
/// <see cref="WrapperBlob.LoadFromSelf"/> yields the <see cref="WrapperBlob.Empty"/>
/// sentinel: parsing + routing are exercised without any real payload, and the
/// completion helper is a documented no-op for the empty blob (no ARP / state
/// writes leak from the test host).
/// </summary>
public sealed class InstallSessionTests
{
    private static readonly string[] Silent = { "/silent" };
    private static readonly string[] Update = { "/Update" };
    private static readonly string[] Uninstall = { "/Uninstall" };
    private static readonly string[] BadFlag = { "/Pnope=1" };

    [Fact]
    public void Create_maps_flags_to_session_state()
    {
        var session = InstallSession.Create(Silent);
        session.Silent.Should().BeTrue();
        session.Mode.Should().Be(WrapperMode.Install);
    }

    [Fact]
    public void Create_unknown_P_flag_throws_usage_exception()
    {
        var act = () => InstallSession.Create(BadFlag);
        act.Should().Throw<UsageException>();
    }

    [Fact]
    public async Task Update_mode_on_a_non_update_enabled_build_exits_not_configured()
    {
        // P12 (T12.3): the un-stamped runtime carries no updates: metadata, so /Update
        // reports "not update-enabled" and returns the dedicated non-configured code —
        // NOT 64 (which now stays reserved for a genuinely-malformed invocation).
        var session = InstallSession.Create(Update);
        var output = new StringWriter();
        var error = new StringWriter();

        var code = await session.RunHeadlessAsync(output, error);

        code.Should().Be(InstallSession.UpdateNotConfiguredExitCode);
        code.Should().NotBe(64);
        error.ToString().Should().Contain("not update-enabled");
    }

    [Fact]
    public async Task Silent_install_of_empty_pipeline_exits_0()
    {
        var session = InstallSession.Create(Silent);
        var output = new StringWriter();
        var error = new StringWriter();

        var code = await session.RunHeadlessAsync(output, error);

        code.Should().Be(0);
    }

    [Fact]
    public async Task RunInstallAsync_on_empty_pipeline_succeeds()
    {
        var session = InstallSession.Create(Silent);
        var events = new List<StepProgress>();
        var progress = new SyncProgress(events.Add);

        var outcome = await session.RunInstallAsync(progress, CancellationToken.None);

        outcome.Success.Should().BeTrue();
        outcome.Error.Should().BeNull();
    }

    [Fact]
    public async Task Uninstall_without_state_exits_1()
    {
        var session = InstallSession.Create(Uninstall);
        var output = new StringWriter();
        var error = new StringWriter();

        var code = await session.RunHeadlessAsync(output, error);

        code.Should().Be(1);
    }

    private sealed class SyncProgress : IProgress<StepProgress>
    {
        private readonly Action<StepProgress> _sink;
        public SyncProgress(Action<StepProgress> sink) => _sink = sink;
        public void Report(StepProgress value) => _sink(value);
    }
}
