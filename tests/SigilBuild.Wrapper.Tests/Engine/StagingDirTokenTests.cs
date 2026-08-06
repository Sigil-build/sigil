namespace SigilBuild.Wrapper.Tests.Engine;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Tests.Helpers;
using Xunit;

/// <summary>
/// Register row R5, engine side: the <c>{staging_dir}</c> brace token that replaced
/// <c>{temp_dir}</c> as the web-installer stub's download destination, and the
/// verify-immediately-before-launch path that closes the gap between the download's
/// checksum and the <c>run_program</c> that executes it.
/// </summary>
/// <remarks>
/// <para>
/// The stub's blob is two independent steps — an <c>http_download</c> then a
/// <c>run_program</c> of the same path — and R5 is the space between them. Two things
/// were wrong and both are asserted here: the path was a pack-time constant derived
/// from the public artifact name (so it could be <b>pre-planted</b>), and the file was
/// closed after hashing and never looked at again (so it could be <b>swapped</b>).
/// </para>
/// <para>
/// <b>Every test here pins the staging siting to a scratch directory</b> via
/// <c>SecureStaging.UseSitingForTesting</c>. Without that, resolving
/// <c>{staging_dir}</c> goes through the production path, which on an <em>elevated</em>
/// host — and CI is elevated — creates directories in the real <c>%ProgramData%</c> and
/// executes binaries out of them. No test in this file touches a real
/// <c>%ProgramData%</c> or <c>%TEMP%</c> path.
/// </para>
/// <para>
/// The siting is pinned unelevated, so the directory is private-but-user-writable rather
/// than administrator-only. The swap protection asserted here does not depend on that,
/// which is the point of doing the re-verification from a held handle at all.
/// </para>
/// </remarks>
public sealed class StagingDirTokenTests
{
    private static StepContext NewContext() =>
        new(new Dictionary<string, object?>(StringComparer.Ordinal));

    private static string Sha256Hex(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    // ── the token ─────────────────────────────────────────────────────────────

    [Fact]
    public void Staging_dir_resolves_to_a_directory_that_exists_and_is_freshly_named()
    {
        using var scratch = new TempDir();
        using var siting = SecureStaging.UseSitingForTesting(scratch.Path);
        var ctx = NewContext();
        try
        {
            var resolved = ctx.ResolvePath("{staging_dir}/Acme-3.2.0-x64-Setup.exe");

            var directory = Path.GetDirectoryName(resolved)!;
            Directory.Exists(directory).Should().BeTrue(
                "the token resolves by CREATING the private directory — an http_download into a path whose " +
                "parent does not exist would only be papered over by the step's own mkdir");
            Path.GetFileName(directory).Should().MatchRegex(
                "^sigil-stage-[0-9a-f]{32}$",
                "the per-run component is a GUID: unguessable is the whole difference from {temp_dir}");
            Path.GetFileName(resolved).Should().Be("Acme-3.2.0-x64-Setup.exe");
        }
        finally
        {
            ctx.ReleaseStaging();
        }
    }

    [Fact]
    public void Staging_dir_is_stable_within_a_run_and_different_between_runs()
    {
        using var scratch = new TempDir();
        using var siting = SecureStaging.UseSitingForTesting(scratch.Path);
        var first = NewContext();
        var second = NewContext();
        try
        {
            // Stable within one run: the download step and the run_program step resolve
            // the same token and MUST land on the same file.
            var download = first.ResolvePath("{staging_dir}/Setup.exe");
            var launch = first.ResolvePath("{staging_dir}/Setup.exe");
            download.Should().Be(launch);

            // Different between runs: a path reused across installs is pre-plantable
            // again, which is the property {temp_dir} had.
            second.ResolvePath("{staging_dir}/Setup.exe").Should().NotBe(download);
        }
        finally
        {
            first.ReleaseStaging();
            second.ReleaseStaging();
        }
    }

    [Fact]
    public void Staging_dir_is_a_fresh_child_of_its_root_not_the_shared_root_itself()
    {
        using var scratch = new TempDir();
        using var siting = SecureStaging.UseSitingForTesting(scratch.Path);
        var ctx = NewContext();
        try
        {
            var stagingDir = ctx.ResolvePath("{staging_dir}");

            stagingDir.Should().NotBe(
                scratch.Path,
                "{temp_dir} put every copy of the stub's download at the same predictable path in a root " +
                "every process of this user can write; the replacement must not do the same");
            Path.GetDirectoryName(stagingDir).Should().Be(
                scratch.Path,
                "the privacy comes from the fresh GUID directory and its protected DACL, not from the root — " +
                "so it is a DIRECT child of whichever root the siting chose");
            ctx.ResolvePath("{temp_dir}").Should().NotBe(
                stagingDir, "the two tokens must never resolve to the same place");
        }
        finally
        {
            ctx.ReleaseStaging();
        }
    }

    [Fact]
    public void Release_removes_the_staging_directory()
    {
        using var scratch = new TempDir();
        using var siting = SecureStaging.UseSitingForTesting(scratch.Path);
        var ctx = NewContext();
        var directory = ctx.ResolvePath("{staging_dir}");
        File.WriteAllBytes(Path.Combine(directory, "Setup.exe"), new byte[] { 1, 2, 3 });

        ctx.ReleaseStaging();

        Directory.Exists(directory).Should().BeFalse(
            "the staging directory lives exactly as long as the run that needed it — a ~200 MB package left " +
            "behind after every install is why this is not simply leaked");
    }

    // ── verify immediately before the launch ──────────────────────────────────

    [Fact]
    public void A_file_this_run_downloaded_and_verified_is_refused_when_its_bytes_changed()
    {
        using var root = new TempDir();
        var ctx = NewContext();
        var path = Path.Combine(root.Path, "Setup.exe");

        var genuine = Encoding.UTF8.GetBytes("the-package-whose-sha256-was-checked");
        File.WriteAllBytes(path, genuine);
        ctx.RecordVerifiedDownload(path, Sha256Hex(genuine));

        // The attacker's window: after the download's checksum, before the launch.
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("attacker-substituted-installer"));

        var open = () => ctx.OpenVerifiedForLaunch(path);

        open.Should().Throw<StagedFileVerificationException>(
                "the sha256 protected the download, not the execution — re-checking it adjacent to the launch " +
                "is what closes the gap")
            .WithMessage("*replaced after verification*");
    }

    [Fact]
    public void A_file_this_run_downloaded_comes_back_as_a_handle_over_the_verified_bytes()
    {
        using var root = new TempDir();
        var ctx = NewContext();
        var path = Path.Combine(root.Path, "Setup.exe");
        var bytes = Encoding.UTF8.GetBytes("the-package-whose-sha256-was-checked");
        File.WriteAllBytes(path, bytes);
        ctx.RecordVerifiedDownload(path, Sha256Hex(bytes));

        using var handle = ctx.OpenVerifiedForLaunch(path);

        handle.Should().NotBeNull();
        handle!.Position.Should().Be(0);

        // For as long as that handle lives, the bytes hashed are the bytes the loader
        // will map: FileShare.Read denies write and delete to everyone else.
        var overwrite = () => File.WriteAllBytes(path, new byte[] { 9 });
        overwrite.Should().Throw<IOException>();
    }

    [Fact]
    public void A_program_this_run_did_not_download_is_left_alone()
    {
        using var root = new TempDir();
        var ctx = NewContext();
        var path = Path.Combine(root.Path, "tool.exe");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });

        ctx.OpenVerifiedForLaunch(path).Should().BeNull(
            "run_program of a payload binary or a system tool is unchanged — only a file THIS run downloaded " +
            "and hash-verified has a digest to re-check against");
    }

    [Fact]
    public void Release_forgets_the_verified_downloads_of_the_run_that_ended()
    {
        using var root = new TempDir();
        var ctx = NewContext();
        var path = Path.Combine(root.Path, "Setup.exe");
        var bytes = Encoding.UTF8.GetBytes("x");
        File.WriteAllBytes(path, bytes);
        ctx.RecordVerifiedDownload(path, Sha256Hex(bytes));

        ctx.ReleaseStaging();

        ctx.OpenVerifiedForLaunch(path).Should().BeNull(
            "a digest confirmed during one engine run says nothing about the same path in a later one");
    }
}
