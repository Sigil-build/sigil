namespace SigilBuild.Wrapper.Tests.Engine;

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Tests.Helpers;
using SigilBuild.Wrapper.Update;
using Xunit;

/// <summary>
/// Pins the ordering that makes all three R11 gates sound, and the lifetime that stops a
/// <c>post_install</c> hook walking around them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these exist.</b> Every gate verifies <b>by path</b> —
/// <c>RunProgramStep</c>, <c>UpdateRunner</c> and <c>PrerequisiteRunner</c> all hand
/// <c>DownloadedBinaryTrust</c> a string. That is correct <em>only</em> because a
/// <see cref="FileShare.Read"/> handle on that path is already open and stays open across
/// the launch, so the bytes <c>WinVerifyTrust</c> reads are provably the bytes the loader
/// will map. Nothing pinned that. Hoisting the trust check above the open, or widening the
/// share mode to <see cref="FileShare.ReadWrite"/>, reopens R5/R11/R12's TOCTOU
/// <em>with the entire suite still green</em> — a security property held by an
/// undocumented ordering is an accident, not a property.
/// </para>
/// <para>
/// The mechanism is a probe substituted for the Authenticode verdict, which therefore runs
/// at exactly the instant of the check. It asserts from inside that moment: reads are
/// admitted (so the mode is not <see cref="FileShare.None"/>, which would break
/// <c>CreateProcess</c> itself), and writes and deletes are refused (so it is not
/// <see cref="FileShare.ReadWrite"/>, and so a handle exists at all).
/// </para>
/// <para>
/// <b>Elevated vs unelevated.</b> Windows share-mode enforcement is a property of the open
/// handles on a file, not of the caller's token: an administrator gets the same sharing
/// violation a standard user does. Each test therefore takes the same branch and asserts
/// the same thing on both host kinds. Nothing here reads an elevation token and no
/// assertion is guarded by one, so neither branch can be vacuous. They are
/// <c>[WindowsFact]</c> because share modes are a Windows concept — that gating is on the
/// OS, never on the token. Every test asserts its probe actually ran, so a gate that
/// silently stopped calling the trust check would fail these rather than pass them.
/// </para>
/// <para>
/// <b>Host damage.</b> Every probe returns <see cref="AuthenticodeStatus.NoSignature"/>,
/// so all three launches are refused and <b>no process is started by any test in this
/// file</b>. Files live in a <see cref="TempDir"/> or in a <c>SecureStaging</c> directory
/// the runner disposes; the assembly-wide <c>NeverStageElevatedForTesting</c> floor keeps
/// the latter out of the real <c>%ProgramData%</c> on an elevated runner.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class LaunchGateOrderingTests
{
    /// <summary>
    /// What the probe saw at the moment of the trust check. All three fields must be true
    /// for the by-path verification to be sound.
    /// </summary>
    private sealed class HandleObservation
    {
        public bool Ran { get; set; }
        public bool ReadAdmitted { get; set; }
        public bool WriteRefused { get; set; }
        public bool DeleteRefused { get; set; }
    }

    private static bool Refused(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// Substitute the Authenticode verdict with a probe that inspects the file's sharing
    /// state. Returns <see cref="AuthenticodeStatus.NoSignature"/> so the launch is
    /// refused and nothing is executed.
    /// </summary>
    private static IDisposable ProbeAt(HandleObservation seen) =>
        DownloadedBinaryTrust.UseStatusForTesting(path =>
        {
            seen.Ran = true;
            seen.ReadAdmitted = !Refused(() => File.ReadAllBytes(path));
            seen.WriteRefused = Refused(() => File.WriteAllBytes(path, Encoding.UTF8.GetBytes("swapped")));
            seen.DeleteRefused = Refused(() => File.Delete(path));
            return AuthenticodeStatus.NoSignature;
        });

    private static void AssertHeldAcrossTheCheck(HandleObservation seen, string site)
    {
        seen.Ran.Should().BeTrue(
            $"the {site} gate must actually call the trust check — a gate that stopped calling it would " +
            "otherwise make this test pass by doing nothing");
        seen.WriteRefused.Should().BeTrue(
            $"at the moment the {site} gate verifies BY PATH, a write-denying handle must already be " +
            "open on that path; without it the bytes verified are not provably the bytes launched, " +
            "which is exactly the TOCTOU R5/R11/R12 close");
        seen.DeleteRefused.Should().BeTrue(
            "delete must be denied too — a swap can be a delete followed by a re-create at the same path");
        seen.ReadAdmitted.Should().BeTrue(
            "and the mode must stay FileShare.Read, not FileShare.None: CreateProcess opens the image " +
            "for read, so denying readers would break the launch this is protecting");
    }

    // ── 1. run_program (the web-stub payload) ─────────────────────────────────

    [WindowsFact("Windows file sharing semantics")]
    public async Task The_run_program_gate_verifies_while_the_handle_is_held()
    {
        using var tmp = new TempDir();
        var program = Path.Combine(tmp.Path, "downloaded.exe");
        var bytes = Encoding.UTF8.GetBytes("a-downloaded-payload");
        await File.WriteAllBytesAsync(program, bytes);
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        var seen = new HandleObservation();
        using (DownloadedBinaryTrust.RequireForTesting(true))
        using (ProbeAt(seen))
        {
            var ctx = new StepContext(new Dictionary<string, object?>(StringComparer.Ordinal));
            ctx.RecordVerifiedDownload(program, sha);

            var result = await new InstallEngine().RunAsync(
                new InstallStep[]
                {
                    new InstallStep.RunProgram(
                        "launch", program, Args: null, Wait: true, Cwd: null,
                        ExpectedExitCodes: new[] { 0 }, TimeoutSeconds: 30, When: null, OnFailure.Fail),
                },
                ctx);

            result.Success.Should().BeFalse("the probe reports NoSignature, so nothing is launched");
        }

        AssertHeldAcrossTheCheck(seen, "run_program");
    }

    // ── 2. The prerequisite runner ────────────────────────────────────────────

    [WindowsFact("Windows file sharing semantics")]
    public async Task The_prerequisite_gate_verifies_while_the_handle_is_held()
    {
        var installerBytes = Encoding.UTF8.GetBytes("a-downloaded-prerequisite");
        using var server = new LoopbackFileServer(installerBytes);
        using var trusted = server.Trust();
        using var tmp = new TempDir();

        var marker = Path.Combine(tmp.Path, "installed.txt");
        var ctx = new StepContext(new Dictionary<string, object?>(), payloadRoot: tmp.Path);
        var prereq = new InstallerPrerequisite(
            Name: "Acme Redist",
            Detect: $"file_exists('{marker.Replace('\\', '/')}')",
            Source: server.Url("/redist.exe"),
            Sha256: Convert.ToHexString(SHA256.HashData(installerBytes)).ToLowerInvariant());

        var seen = new HandleObservation();
        var launched = false;
        PrerequisiteRunner.Launcher launcher = (_, _, _, _) =>
        {
            launched = true;
            return Task.FromResult((0, (string?)null));
        };

        using (ProbeAt(seen))
        {
            var outcome = await PrerequisiteRunner.RunAsync(
                new[] { prereq }, ctx, InstallScope.User, progress: null, launcher, CancellationToken.None);

            outcome.Success.Should().BeFalse();
            launched.Should().BeFalse();
        }

        AssertHeldAcrossTheCheck(seen, "prerequisite");
    }

    // ── 3. The update runner ──────────────────────────────────────────────────

    [WindowsFact("Windows file sharing semantics")]
    public async Task The_update_gate_verifies_while_the_handle_is_held()
    {
        using var tmp = new TempDir();
        var packageBytes = Encoding.UTF8.GetBytes("a-downloaded-setup-payload");
        var sha = Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant();
        var (manifest, signature, key) = UpdateFixtures.SignedManifest("2.0.0", sha);

        var seen = new HandleObservation();
        var launcher = new NeverLauncher();

        using (DownloadedBinaryTrust.RequireForTesting(true))
        using (ProbeAt(seen))
        {
            var runner = new UpdateRunner(
                UpdateFixtures.Fetcher(manifest, signature),
                new UpdateFixtures.WritingDownloader(packageBytes),
                launcher,
                () => UpdateFixtures.Installed("1.0.0"),
                (_, _) => { });

            var code = await runner.RunAsync(UpdateFixtures.Request(key, tmp.Path), CancellationToken.None);

            code.Should().Be(InstallSession.UpdateManifestRejectedExitCode);
            launcher.Called.Should().BeFalse();
        }

        AssertHeldAcrossTheCheck(seen, "update");
    }

    private sealed class NeverLauncher : IChildInstallerLauncher
    {
        public bool Called { get; private set; }

        public Task<int> RunAsync(string exePath, IReadOnlyList<string> args, CancellationToken ct)
        {
            Called = true;
            return Task.FromResult(0);
        }
    }
}
