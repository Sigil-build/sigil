using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using FluentAssertions;
using SigilBuild.Packaging.ExeWrapper;
using SigilBuild.Wrapper.Engine;
using Xunit;

namespace SigilBuild.Packaging.Tests.ExeWrapper;

/// <summary>
/// T18 coverage for the self-contained native-dependency path: the
/// <c>SIGIL_RUNTIME_V1</c> archive built by <see cref="ExeWrapperPackager.BuildRuntimeBytes"/>,
/// its deterministic bytes, the round-trip extraction via
/// <see cref="NativeRuntimeBootstrap.ExtractArchive"/>, that the DLL-search
/// directory P/Invoke path (<see cref="NativeRuntimeBootstrap.AddNativeSearchDirectory"/>)
/// actually makes an extracted DLL loadable (proved headlessly by loading one via
/// <c>LOAD_LIBRARY_SEARCH_USER_DIRS</c>), and that the resource embeds + round-trips
/// in a real stamped PE.
/// </summary>
public sealed partial class NativeRuntimeBootstrapTests
{
    // ---- (a) archive round-trip + determinism -------------------------------

    [Fact]
    public void BuildRuntimeBytes_is_deterministic_and_order_independent()
    {
        var dir = NewTempDir();
        try
        {
            var a = WriteFile(dir, "libSkiaSharp.dll", RandomBytes(4096, seed: 1));
            var b = WriteFile(dir, "av_libglesv2.dll", RandomBytes(2048, seed: 2));
            var c = WriteFile(dir, "libHarfBuzzSharp.dll", RandomBytes(1024, seed: 3));

            var first = ExeWrapperPackager.BuildRuntimeBytes(new[] { a, b, c }, CancellationToken.None);
            // Different caller order must yield byte-identical output (sorted entries).
            var second = ExeWrapperPackager.BuildRuntimeBytes(new[] { c, a, b }, CancellationToken.None);

            first.Should().NotBeEmpty();
            second.Should().Equal(first, "the native-dep archive must be byte-identical regardless of enumeration order");
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void BuildRuntimeBytes_empty_input_yields_empty_archive()
    {
        ExeWrapperPackager.BuildRuntimeBytes(Array.Empty<string>(), CancellationToken.None)
            .Should().BeEmpty();
    }

    [Fact]
    public void ExtractArchive_restores_all_files_with_original_content()
    {
        var src = NewTempDir();
        var dst = NewTempDir();
        try
        {
            var skia = RandomBytes(4096, seed: 11);
            var angle = RandomBytes(2048, seed: 12);
            var harf = RandomBytes(1024, seed: 13);
            var p1 = WriteFile(src, "libSkiaSharp.dll", skia);
            var p2 = WriteFile(src, "av_libglesv2.dll", angle);
            var p3 = WriteFile(src, "libHarfBuzzSharp.dll", harf);

            var archive = ExeWrapperPackager.BuildRuntimeBytes(new[] { p1, p2, p3 }, CancellationToken.None);
            var extracted = NativeRuntimeBootstrap.ExtractArchive(archive, dst);

            extracted.Should().HaveCount(3);
            File.Exists(Path.Combine(dst, "libSkiaSharp.dll")).Should().BeTrue();
            File.Exists(Path.Combine(dst, "av_libglesv2.dll")).Should().BeTrue();
            File.Exists(Path.Combine(dst, "libHarfBuzzSharp.dll")).Should().BeTrue();

            File.ReadAllBytes(Path.Combine(dst, "libSkiaSharp.dll")).Should().Equal(skia);
            File.ReadAllBytes(Path.Combine(dst, "av_libglesv2.dll")).Should().Equal(angle);
            File.ReadAllBytes(Path.Combine(dst, "libHarfBuzzSharp.dll")).Should().Equal(harf);
        }
        finally
        {
            TryDelete(src);
            TryDelete(dst);
        }
    }

    [Fact]
    public void ExtractArchive_is_idempotent()
    {
        var src = NewTempDir();
        var dst = NewTempDir();
        try
        {
            var bytes = RandomBytes(3000, seed: 21);
            var p = WriteFile(src, "libSkiaSharp.dll", bytes);
            var archive = ExeWrapperPackager.BuildRuntimeBytes(new[] { p }, CancellationToken.None);

            var first = NativeRuntimeBootstrap.ExtractArchive(archive, dst);
            var writtenAt = File.GetLastWriteTimeUtc(first[0]);

            // Second extract must not re-write a file already present at the same
            // length (never clobbers a DLL a concurrent process may have loaded).
            var second = NativeRuntimeBootstrap.ExtractArchive(archive, dst);

            second.Should().Equal(first);
            File.GetLastWriteTimeUtc(first[0]).Should().Be(writtenAt);
            File.ReadAllBytes(first[0]).Should().Equal(bytes);
        }
        finally
        {
            TryDelete(src);
            TryDelete(dst);
        }
    }

    [Fact]
    public void ExtractArchive_rejects_zip_slip_entries()
    {
        // Hand-craft a zip whose entry escapes the extraction root.
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("../escape.dll");
            using var s = entry.Open();
            s.Write(new byte[] { 1, 2, 3 });
        }

        var dst = NewTempDir();
        try
        {
            var act = () => NativeRuntimeBootstrap.ExtractArchive(ms.ToArray(), dst);
            act.Should().Throw<InvalidDataException>();
        }
        finally
        {
            TryDelete(dst);
        }
    }

    // ---- (b) DLL-search directory P/Invoke path actually resolves a DLL -----

    [Fact]
    public void AddNativeSearchDirectory_makes_an_extracted_dll_loadable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // Win32 DLL search is a Windows-only concern.
        }

        var src = NewTempDir();
        var dst = NewTempDir();
        try
        {
            // Copy a real, self-standing system DLL under a UNIQUE name so no
            // already-loaded module of that base name can short-circuit the search:
            // resolution must come from our added directory.
            var probeName = $"sigilprobe_{Guid.NewGuid():N}.dll";
            var systemDll = Path.Combine(Environment.SystemDirectory, "winmm.dll");
            File.Exists(systemDll).Should().BeTrue("winmm.dll ships in System32 on all Windows");
            var probeSrc = Path.Combine(src, probeName);
            File.Copy(systemDll, probeSrc);

            // Round-trip it through the real archive + extraction machinery.
            var archive = ExeWrapperPackager.BuildRuntimeBytes(new[] { probeSrc }, CancellationToken.None);
            var extracted = NativeRuntimeBootstrap.ExtractArchive(archive, dst);
            extracted.Should().ContainSingle();
            File.Exists(Path.Combine(dst, probeName)).Should().BeTrue();

            // Register the extraction dir on the native search path...
            NativeRuntimeBootstrap.AddNativeSearchDirectory(dst);

            // ...then prove resolution: LOAD_LIBRARY_SEARCH_USER_DIRS restricts the
            // top-level search to AddDllDirectory dirs only, so a successful load by
            // bare name can only have come from our extraction directory.
            var handle = LoadLibraryExW(probeName, IntPtr.Zero, LoadLibrarySearchUserDirs);
            if (handle == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    $"LoadLibraryEx failed to resolve '{probeName}' from the added search dir");
            }
            try
            {
                handle.Should().NotBe(IntPtr.Zero);
            }
            finally
            {
                FreeLibrary(handle);
            }
        }
        finally
        {
            TryDelete(src);
            TryDelete(dst);
        }
    }

    // ---- (c) resource embeds + round-trips in a real stamped PE -------------

    [Fact]
    public async System.Threading.Tasks.Task Stamped_pe_carries_and_roundtrips_the_runtime_resource()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // BeginUpdateResource is a Win32 API.
        }

        // Any valid PE works for a resource-embed round-trip; use the running test
        // host exe (no AOT publish needed — that keeps `dotnet test` fast).
        var hostExe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(hostExe) || !File.Exists(hostExe) ||
            !hostExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return; // No apphost PE available in this run; other tests cover the archive.
        }

        var tmp = Path.Combine(Path.GetTempPath(), $"sigil-rt-{Guid.NewGuid():N}.exe");
        File.Copy(hostExe, tmp, overwrite: true);
        try
        {
            // Build a real native-dep archive from a fixture DLL and embed it.
            var src = NewTempDir();
            try
            {
                var p = WriteFile(src, "libSkiaSharp.dll", RandomBytes(5000, seed: 31));
                var runtime = ExeWrapperPackager.BuildRuntimeBytes(new[] { p }, CancellationToken.None);
                runtime.Should().NotBeEmpty();

                var blob = Encoding.UTF8.GetBytes("blob");
                var payload = new byte[] { 9 };
                await WrapperResourceWriter.WriteAsync(tmp, blob, payload, runtime, CancellationToken.None);

                var readBack = ResourceReader.Read(tmp, NativeRuntimeBootstrap.RuntimeResourceName);
                readBack.Should().Equal(runtime, "SIGIL_RUNTIME_V1 must round-trip through the PE resource table");

                // And the archive we read back must still extract cleanly.
                var dst = NewTempDir();
                try
                {
                    var extracted = NativeRuntimeBootstrap.ExtractArchive(readBack, dst);
                    extracted.Should().ContainSingle();
                    File.Exists(Path.Combine(dst, "libSkiaSharp.dll")).Should().BeTrue();
                }
                finally
                {
                    TryDelete(dst);
                }
            }
            finally
            {
                TryDelete(src);
            }
        }
        finally
        {
            try { File.Delete(tmp); } catch (IOException) { }
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task Empty_runtime_leaves_no_resource_stamped()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var hostExe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(hostExe) || !File.Exists(hostExe) ||
            !hostExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var tmp = Path.Combine(Path.GetTempPath(), $"sigil-rt-{Guid.NewGuid():N}.exe");
        File.Copy(hostExe, tmp, overwrite: true);
        try
        {
            await WrapperResourceWriter.WriteAsync(
                tmp,
                Encoding.UTF8.GetBytes("blob"),
                new byte[] { 1 },
                Array.Empty<byte>(),
                CancellationToken.None);

            // Reading an absent resource throws (FindResource fails) — that's the
            // un-stamped behaviour the host bootstrap treats as a no-op.
            var act = () => ResourceReader.Read(tmp, NativeRuntimeBootstrap.RuntimeResourceName);
            act.Should().Throw<Win32Exception>();
        }
        finally
        {
            try { File.Delete(tmp); } catch (IOException) { }
        }
    }

    // ---- helpers ------------------------------------------------------------

    private const uint LoadLibrarySearchUserDirs = 0x00000400;

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sigil-t18-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string WriteFile(string dir, string name, byte[] bytes)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] RandomBytes(int count, int seed)
    {
        var buf = new byte[count];
        new Random(seed).NextBytes(buf);
        return buf;
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "LoadLibraryExW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr LoadLibraryExW(string lpLibFileName, IntPtr hFile, uint dwFlags);

    [LibraryImport("kernel32.dll", EntryPoint = "FreeLibrary", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeLibrary(IntPtr hModule);
}
