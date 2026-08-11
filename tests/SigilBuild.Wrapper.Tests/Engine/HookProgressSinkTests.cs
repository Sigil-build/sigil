namespace SigilBuild.Wrapper.Tests.Engine;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Tests.Helpers;
using Xunit;

/// <summary>
/// Register row R56 — a hook phase must have somewhere to report to.
/// </summary>
/// <remarks>
/// <c>ctx.ProgressSink</c> is the channel a step uses for the lines that are not steps:
/// the <c>DownloadedBinaryTrust</c> disarm notice raised by <c>run_program</c>, and
/// <c>SecureStaging</c>'s "this elevated run could not establish an administrator-only
/// staging root". It was set by <see cref="InstallEngine"/> and by nothing else, so
/// every one of those lines raised during a <c>pre_install</c>, <c>post_install</c> or
/// uninstall hook went to <c>null</c>. Both are security refusals, and a refusal that is
/// not logged reads — from the operator's side, and in the /LOG file — exactly like a
/// silent success.
/// </remarks>
public class HookProgressSinkTests
{
    [Fact]
    public async Task A_hook_phase_gives_its_steps_somewhere_to_report_refusals()
    {
        using var tmp = new TempDir();
        var ctx = new StepContext(new Dictionary<string, object?>(StringComparer.Ordinal));
        var sink = new CollectingProgress();

        // One hook step, deliberately inert: a run_program of a path that does not
        // exist, with on_failure: continue. It reaches the step and fails to spawn —
        // nothing is started, nothing on the host is touched — which is all this needs,
        // because the sink must be wired BEFORE the first step runs, not by it.
        var hook = new InstallStep.RunProgram(
            "h1",
            Path.Combine(tmp.Path, "no-such-program.exe"),
            Args: null,
            Wait: true,
            Cwd: null,
            ExpectedExitCodes: new[] { 0 },
            TimeoutSeconds: 30,
            When: null,
            OnFailure.Continue);

        var outcome = await HookRunner.RunAsync(
            "pre_install", new InstallStep[] { hook }, ctx, sink, CancellationToken.None);

        outcome.Success.Should().BeTrue("on_failure: continue keeps the phase going");

        ctx.ProgressSink.Should().BeSameAs(
            sink,
            "a hook step's refusal notices (the run_program disarm notice, SecureStaging's " +
            "elevated-refusal line) are raised on ctx.ProgressSink — with it unset they are " +
            "reported to nothing, and a security refusal nobody logged is indistinguishable " +
            "from a silent success");
    }

    private sealed class CollectingProgress : IProgress<StepProgress>
    {
        public List<StepProgress> Reports { get; } = new();

        public void Report(StepProgress value) => Reports.Add(value);
    }
}
