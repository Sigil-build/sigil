using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using FluentAssertions;
using Microsoft.Win32.SafeHandles;
using SigilBuild.Packaging.ExeWrapper;
using SigilBuild.Wrapper.Engine;
using Xunit;

namespace SigilBuild.Packaging.Tests.ExeWrapper;

/// <summary>
/// Register row R50: the per-run fallback cache directory that R4's fix falls back to
/// when the shared root cannot be established was never reclaimed, so one
/// <c>New-Item</c> at <c>%ProgramData%\sigil-runtime</c> by any unprivileged user armed
/// an unbounded ~18 MB-per-install disk leak.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two constraints govern the fix, and most of this file is about the second.</b>
/// Guards must read from an OPEN HANDLE with reparse-point checks rather than through
/// the path, and a reclaim must never race a concurrent install that is using the
/// directory it is about to delete. Deleting a live installer's mapped native DLLs
/// mid-install is far worse than the leak, so every "must NOT be reclaimed" case below
/// is load-bearing and none of them may be relaxed to make a positive case pass.
/// </para>
/// <para>
/// <b>Everything here uses scratch coordinates under <c>%TEMP%</c>.</b> No test touches
/// the real <c>%ProgramData%\sigil-runtime</c> or <c>%LocalAppData%\Sigil\runtime</c>.
/// CI runs elevated; a test that swept a real cache root would delete from the runner.
/// </para>
/// <para>
/// <b>What cannot be proven unelevated.</b> The administrator-only half of the guard
/// (<c>requireAdminOnlyRoot: true</c>) needs a token this session does not have, so the
/// positive path is exercised with <c>requireAdminOnlyRoot: false</c> — the same shape
/// an unelevated run passes — and the elevated guard is asserted separately, and only in
/// the refusing direction, by <see cref="Handle_and_path_admin_only_predicates_agree"/>.
/// </para>
/// </remarks>
public sealed class NativeRuntimeReclaimTests
{
    private const string LeaseName = ".sigil-runtime-lease";

    private readonly List<(string Message, bool IsError)> _reported = new();

    private void Report(string message, bool isError) => _reported.Add((message, isError));

    // ── the leak itself ───────────────────────────────────────────────────────

    /// <summary>
    /// The decisive case, and the one that fails at the parent commit: a bootstrap run
    /// removes the per-run fallback an earlier, finished run left behind.
    /// </summary>
    [WindowsFact]
    public void Preparing_the_cache_reclaims_an_abandoned_per_run_fallback()
    {
        using var scratch = new Scratch();
        var cacheRoot = Path.Combine(scratch.Path, "sigil-runtime");
        var abandoned = AbandonedFallback(scratch.Path, sizeBytes: 4096);

        NativeRuntimeBootstrap.PrepareCacheDirectory(
            Archive(scratch.Path), cacheRoot, requireAdminOnlyRoot: false, Report);

        Directory.Exists(abandoned).Should().BeFalse(
            "a per-run fallback whose owning run has ended is ~18 MB of dead weight, and " +
            "nothing else will ever remove it (R50)");
    }

    [WindowsFact]
    public void Reclaiming_reports_what_it_removed()
    {
        using var scratch = new Scratch();
        var abandoned = AbandonedFallback(scratch.Path, sizeBytes: 16);

        NativeRuntimeBootstrap.ReclaimAbandonedFallbacks(
            Path.Combine(scratch.Path, "sigil-runtime"), requireAdminOnlyRoot: false, Report)
            .Should().Be(1);

        _reported.Should().Contain(r => r.Message.Contains(abandoned, StringComparison.Ordinal));
    }

    // ── constraint 2: never race a live install ───────────────────────────────

    /// <summary>
    /// The single most important test in this file. A run that is USING a fallback holds
    /// an open, delete-denying handle on its lease file for its whole lifetime. A sweep
    /// must see that and walk away with nothing removed.
    /// </summary>
    [WindowsFact]
    public void A_fallback_whose_lease_is_held_open_is_left_completely_alone()
    {
        using var scratch = new Scratch();
        var live = Path.Combine(scratch.Path, Fallback());
        Directory.CreateDirectory(live);
        var dll = Path.Combine(live, "libSkiaSharp.dll");
        File.WriteAllBytes(dll, RandomBytes(2048, seed: 7));

        // Exactly how the owning process holds it: created, then kept open sharing read
        // only — no Delete, no Write.
        using var lease = new FileStream(
            Path.Combine(live, LeaseName), FileMode.Create, FileAccess.ReadWrite, FileShare.Read);

        NativeRuntimeBootstrap.ReclaimAbandonedFallbacks(
            Path.Combine(scratch.Path, "sigil-runtime"), requireAdminOnlyRoot: false, Report)
            .Should().Be(0);

        Directory.Exists(live).Should().BeTrue("the owning run is still alive");
        File.Exists(dll).Should().BeTrue(
            "and NOTHING may be removed from it — a partial delete of a live install's " +
            "native DLLs is the failure mode this whole design exists to prevent");
    }

    /// <summary>
    /// The pre-lease case: a directory left by a build from before leases existed, whose
    /// owning process is still running with its DLLs mapped. A mapped image cannot be
    /// opened with <see cref="FileShare.None"/>, which is what the probe relies on; an
    /// ordinary open handle stands in for it here.
    /// </summary>
    [WindowsFact]
    public void A_fallback_with_an_open_file_and_no_lease_is_left_completely_alone()
    {
        using var scratch = new Scratch();
        var live = AgedFallback(scratch.Path, out var dll);

        using var mapped = new FileStream(dll, FileMode.Open, FileAccess.Read, FileShare.Read);

        NativeRuntimeBootstrap.ReclaimAbandonedFallbacks(
            Path.Combine(scratch.Path, "sigil-runtime"), requireAdminOnlyRoot: false, Report)
            .Should().Be(0);

        Directory.Exists(live).Should().BeTrue();
        File.Exists(dll).Should().BeTrue("a file another process holds open is proof of use");
    }

    /// <summary>
    /// The creation race. A fallback another run created microseconds ago has no lease
    /// file yet and is still empty, so the file probe would find nothing in use. The
    /// grace period is what stops the sweep deleting it out from under a run that is
    /// about to extract 18 MB into it.
    /// </summary>
    [WindowsFact]
    public void A_freshly_created_unleased_fallback_is_not_reclaimed()
    {
        using var scratch = new Scratch();
        var justCreated = Path.Combine(scratch.Path, Fallback());
        Directory.CreateDirectory(justCreated);

        NativeRuntimeBootstrap.ReclaimAbandonedFallbacks(
            Path.Combine(scratch.Path, "sigil-runtime"), requireAdminOnlyRoot: false, Report)
            .Should().Be(0);

        Directory.Exists(justCreated).Should().BeTrue(
            "an empty, lease-less fallback is indistinguishable from one being created " +
            "right now by a concurrent run, so it waits out the grace period");
    }

    [WindowsFact]
    public void An_aged_unleased_fallback_with_no_open_files_is_reclaimed()
    {
        using var scratch = new Scratch();
        var stale = AgedFallback(scratch.Path, out _);

        NativeRuntimeBootstrap.ReclaimAbandonedFallbacks(
            Path.Combine(scratch.Path, "sigil-runtime"), requireAdminOnlyRoot: false, Report)
            .Should().Be(1);

        Directory.Exists(stale).Should().BeFalse(
            "once it is old enough to not be mid-creation and nothing holds a file in it " +
            "open, it is provably abandoned — this is the pre-lease installed base");
    }

    // ── constraint 1: guards read from an open handle ─────────────────────────

    /// <summary>
    /// A junction planted at a fallback-shaped name must be refused on the reparse-point
    /// bit read from the handle, not followed. Without
    /// <c>FILE_FLAG_OPEN_REPARSE_POINT</c> the guard would be describing the junction's
    /// TARGET while the delete destroyed the target's contents.
    /// </summary>
    [WindowsFact("NTFS directory junctions")]
    public void A_junction_wearing_a_fallback_name_is_refused_not_followed()
    {
        using var scratch = new Scratch();
        var victim = Path.Combine(scratch.Path, "victim");
        Directory.CreateDirectory(victim);
        var precious = Path.Combine(victim, "precious.dll");
        File.WriteAllBytes(precious, RandomBytes(512, seed: 9));

        var link = Path.Combine(scratch.Path, Fallback());
        CreateJunctionOrFail(link, victim);
        // Age it past the grace period so the ONLY thing that can refuse it is the
        // reparse-point check itself.
        Directory.SetCreationTimeUtc(link, DateTime.UtcNow.AddDays(-2));

        NativeRuntimeBootstrap.ReclaimAbandonedFallbacks(
            Path.Combine(scratch.Path, "sigil-runtime"), requireAdminOnlyRoot: false, Report)
            .Should().Be(0);

        File.Exists(precious).Should().BeTrue(
            "following the junction would delete whatever an unprivileged user pointed it at");
        Directory.Exists(link).Should().BeTrue("and the link itself is left alone too");
        _reported.Should().Contain(
            r => r.IsError && r.Message.Contains("reparse point", StringComparison.Ordinal));
    }

    [WindowsFact]
    public void The_shared_root_and_unrelated_neighbours_are_never_candidates()
    {
        using var scratch = new Scratch();
        var cacheRoot = Path.Combine(scratch.Path, "sigil-runtime");
        Directory.CreateDirectory(cacheRoot);

        var bystanders = new[]
        {
            cacheRoot,                                              // the shared root itself
            Path.Combine(scratch.Path, "Sigil"),                    // the install-state store
            Path.Combine(scratch.Path, "sigil-runtime-notaguid"),   // right prefix, wrong shape
            Path.Combine(scratch.Path, "sigil-runtime-" + new string('g', 32)), // non-hex
            Path.Combine(scratch.Path, "sigil-runtime-" + new string('a', 31)), // too short
        };

        foreach (var dir in bystanders)
        {
            Directory.CreateDirectory(dir);
            Directory.SetCreationTimeUtc(dir, DateTime.UtcNow.AddDays(-2));
        }

        NativeRuntimeBootstrap.ReclaimAbandonedFallbacks(cacheRoot, requireAdminOnlyRoot: false, Report)
            .Should().Be(0);

        foreach (var dir in bystanders)
        {
            Directory.Exists(dir).Should().BeTrue(
                "'{0}' is not a name the fallback path ever produces, so the sweep must not " +
                "claim it — the shared root in particular is where every install's cache lives",
                dir);
        }
    }

    /// <summary>
    /// The handle-based administrator-only predicate added for R50 must reach exactly the
    /// same verdict as the path-based one lanes S2/S3 gate SYSTEM-level step targets on,
    /// on the three directories that predicate was hand-verified against.
    /// </summary>
    [WindowsFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void Handle_and_path_admin_only_predicates_agree()
    {
        using var scratch = new Scratch();

        var cases = new (string Path, bool Expected, string Why)[]
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), true,
                "TrustedInstaller-owned and admin-only — machine installs land here"),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), false,
                "%ProgramData% grants BUILTIN\\Users (CI)(WD,AD): admin-OWNED but user-WRITABLE"),
            (scratch.Path, false,
                "a directory this unelevated session created is owned by this user"),
        };

        foreach (var (path, expected, why) in cases)
        {
            StateDirectorySecurity.IsAdminOnlyWritable(path).Should().Be(
                expected, "path-based predicate, '{0}': {1}", path, why);

            using var handle = DirectoryHandle.OpenNoFollow(path, LeaseName);
            handle.Should().NotBeNull("'{0}' must be openable for the handle-based check", path);

            StateDirectorySecurity.IsAdminOnlyWritableHandle(handle!.Handle).Should().Be(
                expected,
                "handle-based predicate must agree with the path-based one on '{0}' — a " +
                "disagreement means R50's guard and the S2/S3 privileged-target guard have " +
                "drifted apart: {1}", path, why);
        }
    }

    [WindowsFact]
    public void An_elevated_sweep_will_not_touch_a_directory_it_cannot_prove_admin_only()
    {
        using var scratch = new Scratch();
        var stale = AgedFallback(scratch.Path, out var dll);

        // requireAdminOnlyRoot: true is what an elevated run passes. This unelevated
        // session cannot produce a directory that passes the predicate, so the sweep must
        // refuse — the same direction as production refusing a squatted one.
        NativeRuntimeBootstrap.ReclaimAbandonedFallbacks(
            Path.Combine(scratch.Path, "sigil-runtime"), requireAdminOnlyRoot: true, Report)
            .Should().Be(0);

        Directory.Exists(stale).Should().BeTrue();
        File.Exists(dll).Should().BeTrue(
            "a candidate that cannot be proven administrator-only is left strictly alone, " +
            "never deleted — the leak is the accepted cost of that");
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static string Fallback() => "sigil-runtime-" + Guid.NewGuid().ToString("N");

    /// <summary>
    /// A fallback left by a run that ended: it has a lease FILE (the owning process wrote
    /// one) but no live handle on it (that process is gone). No ageing needed — the lease
    /// file is what makes it decidable.
    /// </summary>
    private static string AbandonedFallback(string parent, int sizeBytes)
    {
        var dir = Path.Combine(parent, Fallback());
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, LeaseName), Array.Empty<byte>());
        var key = Path.Combine(dir, new string('0', 64));
        Directory.CreateDirectory(key);
        File.WriteAllBytes(Path.Combine(key, "libSkiaSharp.dll"), RandomBytes(sizeBytes, seed: 5));
        return dir;
    }

    /// <summary>
    /// A fallback from before leases existed: no lease file, and aged past the grace
    /// period so the ONLY remaining question is whether anything holds its files open.
    /// </summary>
    private static string AgedFallback(string parent, out string file)
    {
        var dir = Path.Combine(parent, Fallback());
        Directory.CreateDirectory(dir);
        file = Path.Combine(dir, "libSkiaSharp.dll");
        File.WriteAllBytes(file, RandomBytes(1024, seed: 6));
        Directory.SetCreationTimeUtc(dir, DateTime.UtcNow.AddDays(-2));
        return dir;
    }

    /// <summary>A real SIGIL_RUNTIME_V1 archive over one fake native DLL.</summary>
    private static byte[] Archive(string scratch)
    {
        var src = Path.Combine(scratch, "archive-src");
        Directory.CreateDirectory(src);
        var dll = Path.Combine(src, "libSkiaSharp.dll");
        File.WriteAllBytes(dll, RandomBytes(4096, seed: 101));
        return ExeWrapperPackager.BuildRuntimeBytes(new[] { dll }, CancellationToken.None);
    }

    private static byte[] RandomBytes(int count, int seed)
    {
        var bytes = new byte[count];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    /// <summary>
    /// A real NTFS junction, or a loud failure. <c>Directory.CreateSymbolicLink</c> needs
    /// elevation or Developer Mode; a junction needs neither, which is exactly why it is
    /// the redirection primitive worth defending against.
    /// </summary>
    private static void CreateJunctionOrFail(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("mklink");
        psi.ArgumentList.Add("/J");
        psi.ArgumentList.Add(link);
        psi.ArgumentList.Add(target);

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        process.ExitCode.Should().Be(0, "mklink /J failed: {0}{1}", stdout, stderr);
        (File.GetAttributes(link) & FileAttributes.ReparsePoint).Should().NotBe(
            0, "a test that silently degraded to a plain directory would pass vacuously");
    }

    private sealed class Scratch : IDisposable
    {
        public Scratch()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"sigil-r50-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
