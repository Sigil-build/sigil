using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using FluentAssertions;
using SigilBuild.Packaging.ExeWrapper;
using SigilBuild.Wrapper.Engine;
using Xunit;

namespace SigilBuild.Packaging.Tests.ExeWrapper;

/// <summary>
/// Register row R4: the elevated wizard used to adopt its content-keyed native-runtime
/// cache directory on the strength of a <c>.sigil-runtime-complete</c> marker file, then
/// register that directory on the process DLL search path. Any process running as the
/// user could pre-create the (derivable) directory, drop a hostile
/// <c>libSkiaSharp.dll</c> in it, <c>touch</c> the marker, and be loaded elevated.
/// </summary>
/// <remarks>
/// <para>
/// The decisive case is
/// <see cref="A_planted_dll_behind_a_valid_completion_marker_is_never_adopted"/> — the
/// plan's test, verbatim: pre-create the cache directory with a bogus DLL <em>and</em> a
/// valid marker, and assert the bootstrap does not adopt it.
/// </para>
/// <para>
/// <b>Everything here uses scratch coordinates.</b> No test touches the real
/// <c>%LocalAppData%\Sigil\runtime</c> or <c>%ProgramData%\Sigil</c> cache, and none
/// writes a DLL anywhere the OS would search — the cache root is always a throwaway
/// directory under <c>%TEMP%</c>. CI runs elevated; a test that reached for the real
/// cache would corrupt the runner.
/// </para>
/// <para>
/// <b>What this file cannot prove locally.</b> The elevated branch is exercised by
/// passing <c>requireAdminOnlyRoot: true</c> explicitly, which is exactly what an
/// elevated run passes — but the <em>positive</em> outcome (a hardened directory under
/// <c>%ProgramData%</c> that really does pass the admin-only predicate) needs a token
/// this unelevated session does not have. Locally only the refusal is observable, and
/// that is what is asserted.
/// </para>
/// </remarks>
public sealed class NativeRuntimeCacheTrustTests
{
    private const string MarkerName = ".sigil-runtime-complete";
    private const string SkiaName = "libSkiaSharp.dll";

    private readonly List<(string Message, bool IsError)> _reported = new();

    /// <summary>A real SIGIL_RUNTIME_V1 archive over two fake native DLLs.</summary>
    private static (byte[] Archive, byte[] Skia, byte[] Angle) Archive(string scratch)
    {
        var skia = RandomBytes(4096, seed: 101);
        var angle = RandomBytes(2048, seed: 102);
        var src = Path.Combine(scratch, "src");
        Directory.CreateDirectory(src);
        File.WriteAllBytes(Path.Combine(src, SkiaName), skia);
        File.WriteAllBytes(Path.Combine(src, "av_libglesv2.dll"), angle);

        var archive = ExeWrapperPackager.BuildRuntimeBytes(
            new[] { Path.Combine(src, SkiaName), Path.Combine(src, "av_libglesv2.dll") },
            CancellationToken.None);
        return (archive, skia, angle);
    }

    // ── the decisive case ─────────────────────────────────────────────────────

    [Fact]
    public void A_planted_dll_behind_a_valid_completion_marker_is_never_adopted()
    {
        using var scratch = new Scratch();
        var (archive, skia, _) = Archive(scratch.Path);
        var root = Path.Combine(scratch.Path, "runtime");

        // The attacker's move, in full: derive the content-keyed directory name from the
        // archive (it is readable straight out of the setup exe), create it, plant a
        // hostile libSkiaSharp.dll, and touch the completion marker so extraction is
        // skipped wholesale. Pre-fix this directory went onto the DLL search path as-is.
        var planted = Path.Combine(root, ArchiveKey(archive));
        Directory.CreateDirectory(planted);
        var hostile = new byte[] { 0x4D, 0x5A, 0xDE, 0xAD, 0xBE, 0xEF };
        File.WriteAllBytes(Path.Combine(planted, SkiaName), hostile);
        File.WriteAllBytes(Path.Combine(planted, MarkerName), Array.Empty<byte>());

        var prepared = NativeRuntimeBootstrap.PrepareCacheDirectory(
            archive, root, requireAdminOnlyRoot: false, Report);

        prepared.Should().Be(planted, "the cache is still content-keyed — it is the CONTENTS that are re-established");
        File.ReadAllBytes(Path.Combine(prepared, SkiaName)).Should().Equal(
            skia,
            "the planted DLL must be replaced by the embedded archive's bytes before the directory is ever " +
            "put on the DLL search path — a marker file an attacker can touch is not proof of anything");
        File.ReadAllBytes(Path.Combine(prepared, SkiaName)).Should().NotEqual(hostile);
        _reported.Should().Contain(
            r => r.IsError && r.Message.Contains("does not match the embedded archive", StringComparison.Ordinal),
            "discarding a cache directory that did not hold our bytes is exactly the line an operator needs");
    }

    [Fact]
    public void A_planted_extra_dll_the_archive_never_contained_is_removed()
    {
        using var scratch = new Scratch();
        var (archive, skia, angle) = Archive(scratch.Path);
        var root = Path.Combine(scratch.Path, "runtime");

        // Subtler than swapping a known DLL: leave the genuine files alone and add one
        // more. The loader resolves by name across the whole directory, so a planted
        // version.dll beside untouched Skia binaries is just as loadable.
        var planted = Path.Combine(root, ArchiveKey(archive));
        Directory.CreateDirectory(planted);
        File.WriteAllBytes(Path.Combine(planted, SkiaName), skia);
        File.WriteAllBytes(Path.Combine(planted, "av_libglesv2.dll"), angle);
        File.WriteAllBytes(Path.Combine(planted, "version.dll"), new byte[] { 1, 2, 3 });
        File.WriteAllBytes(Path.Combine(planted, MarkerName), Array.Empty<byte>());

        var prepared = NativeRuntimeBootstrap.PrepareCacheDirectory(
            archive, root, requireAdminOnlyRoot: false, Report);

        File.Exists(Path.Combine(prepared, "version.dll")).Should().BeFalse(
            "a file the archive never contained must not survive into the directory the loader searches");
        File.ReadAllBytes(Path.Combine(prepared, SkiaName)).Should().Equal(skia);
    }

    [Fact]
    public void A_truncated_dll_of_the_archived_length_is_not_adopted()
    {
        using var scratch = new Scratch();
        var (archive, skia, _) = Archive(scratch.Path);
        var root = Path.Combine(scratch.Path, "runtime");

        // The pre-fix incremental path compared file LENGTH only — a value the attacker
        // controls exactly. Same length, different bytes, no marker at all.
        var planted = Path.Combine(root, ArchiveKey(archive));
        Directory.CreateDirectory(planted);
        var sameLength = new byte[skia.Length];
        Array.Fill(sameLength, (byte)0x41);
        File.WriteAllBytes(Path.Combine(planted, SkiaName), sameLength);

        var prepared = NativeRuntimeBootstrap.PrepareCacheDirectory(
            archive, root, requireAdminOnlyRoot: false, Report);

        File.ReadAllBytes(Path.Combine(prepared, SkiaName)).Should().Equal(
            skia, "length is not identity — the comparison must be over content");
    }

    // ── the happy paths still hold ────────────────────────────────────────────

    [Fact]
    public void A_first_run_extracts_verifies_and_writes_the_marker()
    {
        using var scratch = new Scratch();
        var (archive, skia, angle) = Archive(scratch.Path);
        var root = Path.Combine(scratch.Path, "runtime");

        var prepared = NativeRuntimeBootstrap.PrepareCacheDirectory(
            archive, root, requireAdminOnlyRoot: false, Report);

        File.ReadAllBytes(Path.Combine(prepared, SkiaName)).Should().Equal(skia);
        File.ReadAllBytes(Path.Combine(prepared, "av_libglesv2.dll")).Should().Equal(angle);
        File.Exists(Path.Combine(prepared, MarkerName)).Should().BeTrue(
            "the marker is written only AFTER the extraction was verified, which is what makes it a usable fast path");
        _reported.Should().BeEmpty("a clean first extraction has nothing to report");
    }

    [Fact]
    public void An_intact_cache_is_reused_without_rewriting_its_files()
    {
        using var scratch = new Scratch();
        var (archive, _, _) = Archive(scratch.Path);
        var root = Path.Combine(scratch.Path, "runtime");

        var first = NativeRuntimeBootstrap.PrepareCacheDirectory(
            archive, root, requireAdminOnlyRoot: false, Report);
        var writtenAt = File.GetLastWriteTimeUtc(Path.Combine(first, SkiaName));

        var second = NativeRuntimeBootstrap.PrepareCacheDirectory(
            archive, root, requireAdminOnlyRoot: false, Report);

        second.Should().Be(first);
        File.GetLastWriteTimeUtc(Path.Combine(second, SkiaName)).Should().Be(
            writtenAt, "a verified cache must never be re-extracted — a DLL a concurrent process loaded stays put");
        _reported.Should().BeEmpty("reusing an intact cache is not an event");
    }

    // ── the elevated branch: refuse, do not degrade ───────────────────────────

    [Fact]
    public void An_elevated_run_refuses_a_cache_root_that_is_not_administrator_only()
    {
        using var scratch = new Scratch();
        var (archive, _, _) = Archive(scratch.Path);
        // A root under %TEMP%: created by this unelevated process, therefore owned and
        // writable by it — precisely the shape %LocalAppData% has, and precisely what an
        // elevated run must not extract native DLLs into.
        var root = Path.Combine(scratch.Path, "runtime");

        var prepare = () => NativeRuntimeBootstrap.PrepareCacheDirectory(
            archive, root, requireAdminOnlyRoot: true, Report);

        if (OperatingSystem.IsWindows())
        {
            prepare.Should().Throw<NativeRuntimeTrustException>(
                    "an elevated run that cannot site its cache where only administrators can write must refuse, " +
                    "not extract into a directory a non-administrator can rewrite between the check and the load")
                .WithMessage("*administrator-only writable*");
        }
        else
        {
            prepare.Should().Throw<NativeRuntimeTrustException>(
                "an administrator-only root is a Windows concept; off Windows the elevated request cannot be honoured");
        }

        Directory.Exists(Path.Combine(root, ArchiveKey(archive))).Should().BeFalse(
            "nothing is extracted into a root that failed the check");
    }

    /// <summary>
    /// The elevated cache root is a <b>direct child of <c>%ProgramData%</c></b> and is
    /// deliberately not under <c>%ProgramData%\Sigil</c>.
    /// </summary>
    /// <remarks>
    /// That path is the install-state store's root, must not be repaired from here, and
    /// can be created by any unprivileged user (register row R1's attack) — so depending
    /// on it turned this component's refusal into a denial of service anyone could
    /// trigger against every elevated GUI install. <c>sigil-runtime</c> is this
    /// component's own directory, which a hardened create may repair and take ownership
    /// of, so a squat costs nothing. <c>%ProgramData%</c> grants <c>BUILTIN\Users</c>
    /// create-child but not delete-child, so a directory that is ours stays ours.
    /// </remarks>
    [Fact]
    public void The_elevated_cache_root_does_not_depend_on_the_squattable_state_root()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        var elevated = NativeRuntimeBootstrap.ResolveCacheRoot(elevated: true);

        elevated.Should().Be(Path.Combine(programData, "sigil-runtime"));
        Path.GetDirectoryName(elevated).Should().Be(
            programData,
            "a direct child of %ProgramData% descends through no directory a non-administrator could have " +
            "created first");
        elevated.Should().NotStartWith(
            Path.Combine(programData, "Sigil") + Path.DirectorySeparatorChar,
            "%ProgramData%\\Sigil belongs to the install-state store: this component may not repair it, and " +
            "any user can create it, so requiring it to be trusted would be a denial of service on demand");

        NativeRuntimeBootstrap.ResolveCacheRoot(elevated: false).Should().Be(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Sigil", "runtime"),
            "unelevated there is no privilege boundary, so the historical per-user cache is unchanged");
    }

    [Fact]
    public void A_squatted_state_root_is_neither_consulted_nor_touched()
    {
        using var scratch = new Scratch();
        var (archive, skia, _) = Archive(scratch.Path);

        // The squat, as an unprivileged user would leave it: a plain, this-user-owned
        // directory named Sigil beside where the cache root goes.
        var sigil = Path.Combine(scratch.Path, "Sigil");
        Directory.CreateDirectory(sigil);

        var root = Path.Combine(scratch.Path, "sigil-runtime");
        var prepared = NativeRuntimeBootstrap.PrepareCacheDirectory(
            archive, root, requireAdminOnlyRoot: false, Report);

        File.ReadAllBytes(Path.Combine(prepared, SkiaName)).Should().Equal(
            skia, "the extraction proceeds — a squatted state root is not on this path at all");
        Directory.GetFileSystemEntries(sigil).Should().BeEmpty(
            "the state root is neither written to nor repaired by the native runtime bootstrap");
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private void Report(string message, bool isError) => _reported.Add((message, isError));

    private static string ArchiveKey(byte[] archive) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(archive)).ToLowerInvariant();

    private static byte[] RandomBytes(int count, int seed)
    {
        var buf = new byte[count];
        new Random(seed).NextBytes(buf);
        return buf;
    }

    /// <summary>A throwaway directory under %TEMP%; never a real cache location.</summary>
    private sealed class Scratch : IDisposable
    {
        public Scratch()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"sigil-r4-{Guid.NewGuid():N}");
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
