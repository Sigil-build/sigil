namespace SigilBuild.Wrapper.Tests.Engine;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Tests.Helpers;
using Xunit;

/// <summary>
/// The <c>post_install</c> hook phase runs on the SAME <see cref="StepContext"/> as the
/// install body, but <em>after</em> <see cref="InstallEngine"/>'s <c>finally</c> has
/// already released that run's staging. Two consequences, both closed here.
/// </summary>
/// <remarks>
/// <para>
/// <b>1. A silent bypass.</b> The release cleared the verified-download record, so a hook
/// <c>run_program</c> of a binary the install body downloaded found nothing, took the
/// "this run did not download it" path, and was launched with <b>no SHA-256 re-check, no
/// held handle and no Authenticode verdict</b> — indistinguishable, from the outside, from
/// a launch that had all three. That is the same shape as the disarmed gate fixed one
/// round earlier: absent rather than refusing, with nothing said. R11's text is "before
/// launching ANY downloaded binary"; a hook is not an exception to it.
/// </para>
/// <para>
/// <b>2. An unbounded leak.</b> Resolving <c>{staging_dir}</c> in such a hook minted a
/// second <c>SecureStaging</c> that nothing ever disposed — a hardened directory per
/// install, in <c>%ProgramData%</c> on an elevated run.
/// </para>
/// <para>
/// <b>Elevated vs unelevated.</b> Neither test reads an elevation token, and no assertion
/// is guarded by one. The refusal is driven by the context's own bookkeeping, and the
/// directory's existence is a plain filesystem fact; both assert identically on an
/// elevated and an unelevated host. Staging siting is pinned to a scratch root by
/// <c>SecureStaging.UseSitingForTesting</c> plus the assembly-wide
/// <c>NeverStageElevatedForTesting</c> floor, so the elevated branch cannot reach the real
/// <c>%ProgramData%</c> on either. Plain <c>[Fact]</c>: nothing here is Windows-specific.
/// </para>
/// <para>
/// <b>Host damage.</b> No process is started — the refusal happens before
/// <c>Process.Start</c>, and the leak test runs no <c>run_program</c> at all. Everything
/// written lives under a <see cref="TempDir"/>.
/// </para>
/// </remarks>
public sealed class HookStagingLifetimeTests
{
    private static InstallStep.RunProgram Launch(string program) =>
        new("hook_launch", program, Args: null, Wait: true, Cwd: null,
            ExpectedExitCodes: new[] { 0 }, TimeoutSeconds: 30, When: null, OnFailure.Fail);

    /// <summary>
    /// The decisive case: the install body downloads and verifies a binary, the engine
    /// releases, and a <c>post_install</c> hook then tries to run that exact file.
    /// </summary>
    [Fact]
    public async Task A_post_install_hook_cannot_launch_a_binary_the_install_body_downloaded()
    {
        using var tmp = new TempDir();
        var program = Path.Combine(tmp.Path, "downloaded.exe");
        var bytes = Encoding.UTF8.GetBytes("a-binary-the-install-body-downloaded");
        await File.WriteAllBytesAsync(program, bytes);
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        var ctx = new StepContext(new Dictionary<string, object?>(StringComparer.Ordinal));

        // Exactly what HttpDownloadStep does, then exactly what InstallEngine's finally
        // does. Driving the two directly keeps the test about the LIFETIME rather than
        // about http_download's plumbing, which HttpDownloadIntegrationTests already owns.
        ctx.RecordVerifiedDownload(program, sha);
        ctx.ReleaseStaging();

        var outcome = await HookRunner.RunAsync(
            "post_install", new InstallStep[] { Launch(program) }, ctx, progress: null, CancellationToken.None);

        outcome.Success.Should().BeFalse(
            "a hook that runs a binary the install body downloaded must not get a launch with none of " +
            "the guarantees that binary's own step would have had");
        outcome.Error.Should().Contain("was downloaded by this install");
        outcome.Error.Should().Contain("refusing to run it");
    }

    /// <summary>
    /// The positive control. Without it, the refusal above could equally mean "hooks can no
    /// longer run programs". A hook that downloads its own binary re-establishes the
    /// record, so it keeps the full guarantee and is gated normally rather than refused
    /// for the lifetime reason.
    /// </summary>
    [Fact]
    public async Task A_hook_that_records_its_own_download_is_gated_normally_not_refused()
    {
        using var tmp = new TempDir();
        var program = Path.Combine(tmp.Path, "downloaded.exe");
        var bytes = Encoding.UTF8.GetBytes("a-binary-the-hook-downloaded-itself");
        await File.WriteAllBytesAsync(program, bytes);
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        var ctx = new StepContext(new Dictionary<string, object?>(StringComparer.Ordinal));
        ctx.RecordVerifiedDownload(program, sha);
        ctx.ReleaseStaging();

        // The hook downloads it again — a live record must win over the retired one.
        ctx.RecordVerifiedDownload(program, sha);

        var outcome = await HookRunner.RunAsync(
            "post_install", new InstallStep[] { Launch(program) }, ctx, progress: null, CancellationToken.None);

        outcome.Success.Should().BeFalse("the file is not a valid PE image, so the OS refuses it");
        outcome.Error.Should().NotContain(
            "was downloaded by this install",
            "a re-recorded download has its guarantee back and must not be refused for the lifetime reason");
        outcome.Error.Should().Contain(
            "failed to start",
            "the only thing left stopping it is the OS refusing a non-PE image — which is how this test " +
            "proves the gate opened without executing anything");
    }

    /// <summary>
    /// An ordinary hook <c>run_program</c> of something this run never downloaded — a
    /// payload binary, a system tool — is the common case and must be untouched.
    /// </summary>
    [Fact]
    public async Task A_hook_launching_something_this_run_never_downloaded_is_unaffected()
    {
        using var tmp = new TempDir();
        var program = Path.Combine(tmp.Path, "bundled.exe");
        await File.WriteAllBytesAsync(program, Encoding.UTF8.GetBytes("never-downloaded"));

        var ctx = new StepContext(new Dictionary<string, object?>(StringComparer.Ordinal));
        ctx.RecordVerifiedDownload(Path.Combine(tmp.Path, "something-else.exe"), new string('a', 64));
        ctx.ReleaseStaging();

        var outcome = await HookRunner.RunAsync(
            "post_install", new InstallStep[] { Launch(program) }, ctx, progress: null, CancellationToken.None);

        outcome.Error.Should().NotContain("was downloaded by this install");
        outcome.Error.Should().Contain("failed to start", "it reaches Process.Start, and is a non-PE file");
    }

    /// <summary>
    /// The leak half: a <c>{staging_dir}</c> first resolved in a hook phase that runs after
    /// the release must be reclaimed when that phase ends, not left behind forever.
    /// </summary>
    [Fact]
    public async Task A_staging_dir_resolved_in_a_post_install_hook_is_reclaimed_when_the_phase_ends()
    {
        using var scratch = new TempDir();
        using var siting = SecureStaging.UseSitingForTesting(scratch.Path);

        var ctx = new StepContext(new Dictionary<string, object?>(StringComparer.Ordinal));
        ctx.ReleaseStaging(); // the engine's finally has already run

        string? hookStagingDir = null;

        // A hook step that resolves the token, exactly as an http_download of
        // "{staging_dir}/x.exe" would, and records where it landed.
        var probe = new InstallStep.RunProgram(
            "resolve", "{staging_dir}/nothing.exe", Args: null, Wait: true, Cwd: null,
            ExpectedExitCodes: new[] { 0 }, TimeoutSeconds: 30, When: null, OnFailure.Continue);

        var outcome = await HookRunner.RunAsync(
            "post_install",
            new InstallStep[] { probe },
            ctx,
            progress: new CapturingProgress(_ => hookStagingDir ??= TryReadStagingDir(ctx, scratch.Path)),
            CancellationToken.None);

        // The step itself fails (there is no such program) — irrelevant; on_failure:
        // continue keeps the phase going and the token was still resolved.
        outcome.Success.Should().BeTrue();
        hookStagingDir.Should().NotBeNull("the hook must have resolved {staging_dir} for this to prove anything");
        hookStagingDir.Should().StartWith(scratch.Path, "no test may stage into a real %ProgramData% path");
        Directory.Exists(hookStagingDir!).Should().BeFalse(
            "a {staging_dir} minted after the install body released had no owner and nothing ever disposed " +
            "it — one hardened directory leaked per install, unbounded");
    }

    /// <summary>
    /// Reads the hook phase's staging directory by resolving the token again — the same
    /// instance the step just used, because it is created once per phase and cached.
    /// </summary>
    private static string? TryReadStagingDir(StepContext ctx, string scratchRoot)
    {
        var resolved = ctx.ResolvePath("{staging_dir}");
        return resolved.StartsWith(scratchRoot, StringComparison.OrdinalIgnoreCase) ? resolved : null;
    }

    private sealed class CapturingProgress : IProgress<StepProgress>
    {
        private readonly Action<StepProgress> _onReport;

        public CapturingProgress(Action<StepProgress> onReport) => _onReport = onReport;

        public void Report(StepProgress value) => _onReport(value);
    }
}
