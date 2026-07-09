using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Engine;

/// <summary>
/// Verifies the <see cref="IProgress{T}"/> reporting + rollback behaviour the
/// GUI Failed screen and the headless exit-1 / exit-2 paths both depend on: the
/// engine emits per-step log lines, an <c>error:</c> + <c>rollback:</c> pair on
/// failure, and unwinds prior steps.
/// </summary>
public sealed class InstallEngineProgressTests
{
    [Fact]
    public async Task Step_failure_reports_error_rollback_and_undoes_prior_steps()
    {
        var root = NewTempRoot();
        try
        {
            var createdDir = Path.Combine(root, "sub");
            var steps = new InstallStep[]
            {
                new InstallStep.DirectoryCreate("mk", createdDir, When: null, OnFailure.Rollback),
                new InstallStep.FileCopy(
                    "cp",
                    From: Path.Combine(root, "missing-src", "**"),
                    To: Path.Combine(root, "dest"),
                    Overwrite: true,
                    When: null,
                    OnFailure.Rollback),
            };

            var events = new List<StepProgress>();
            var progress = new SyncProgress(events.Add);

            var result = await new InstallEngine().RunAsync(
                Array.Empty<InstallStep>(), steps, Array.Empty<InstallStep>(),
                StepContext.Empty, progress, CancellationToken.None);

            result.Success.Should().BeFalse();
            Messages(events).Should().Contain(m => m.StartsWith("mkdir", StringComparison.Ordinal));
            Messages(events).Should().Contain(m => m.StartsWith("error:", StringComparison.Ordinal));
            Messages(events).Should().Contain(m => m.StartsWith("rollback:", StringComparison.Ordinal));

            Directory.Exists(createdDir).Should().BeFalse("the created directory must be rolled back");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Cancellation_rolls_back_and_throws_operation_cancelled()
    {
        var root = NewTempRoot();
        try
        {
            var steps = new InstallStep[]
            {
                new InstallStep.DirectoryCreate("mk", Path.Combine(root, "sub"), When: null, OnFailure.Rollback),
            };

            var events = new List<StepProgress>();
            var progress = new SyncProgress(events.Add);

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var act = async () => await new InstallEngine().RunAsync(
                Array.Empty<InstallStep>(), steps, Array.Empty<InstallStep>(),
                StepContext.Empty, progress, cts.Token);

            await act.Should().ThrowAsync<OperationCanceledException>();
            Messages(events).Should().Contain(m => m.StartsWith("rollback:", StringComparison.Ordinal));
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static IEnumerable<string> Messages(IEnumerable<StepProgress> events) =>
        events.Where(e => e.Message is not null).Select(e => e.Message!);

    private static string NewTempRoot() =>
        Path.Combine(Path.GetTempPath(), "sigil-t2-" + Guid.NewGuid().ToString("N"));

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (IOException) { /* best-effort */ }
        catch (UnauthorizedAccessException) { /* best-effort */ }
    }

    private sealed class SyncProgress : IProgress<StepProgress>
    {
        private readonly Action<StepProgress> _sink;
        public SyncProgress(Action<StepProgress> sink) => _sink = sink;
        public void Report(StepProgress value) => _sink(value);
    }
}
