// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using FluentAssertions;
using SigilBuild.Wrapper.Engine;
using Xunit;

namespace SigilBuild.Packaging.Tests.ExeWrapper;

/// <summary>
/// The wizard host must load Skia, HarfBuzz and ANGLE from the verified native-runtime
/// cache and never from the directory the exe runs in: a <c>libSkiaSharp.dll</c> planted
/// beside <c>Setup.exe</c> in Downloads, or the installed application's own copy beside
/// <c>uninstall.exe</c>, must not answer first. (R102)
/// </summary>
/// <remarks>
/// The loading tests use a copy of a System32 DLL under a <b>unique</b> file name rather
/// than the real <c>libSkiaSharp.dll</c> name: a module of that base name loaded into this
/// test process could be picked up by SkiaSharp's own probing in a concurrently running
/// packaging test. The name-to-file mapping is asserted on the real names separately.
/// </remarks>
public sealed partial class NativeRuntimePinningTests
{
    // ── the search flags ─────────────────────────────────────────────────────

    [Fact]
    public void Default_search_flags_are_System32_and_user_dirs_only()
    {
        // Arrange
        const uint system32 = 0x00000800;
        const uint userDirs = 0x00000400;
        const uint applicationDir = 0x00000200;
        const uint defaultDirs = 0x00001000;

        // Act
        var flags = NativeRuntimeBootstrap.DefaultDllSearchFlags;

        // Assert
        flags.Should().Be(system32 | userDirs);
        (flags & applicationDir).Should().Be(0u, "the exe's own directory must never be searched");
        (flags & defaultDirs).Should().Be(0u, "LOAD_LIBRARY_SEARCH_DEFAULT_DIRS includes the application directory");
    }

    // ── library name → pinned file ───────────────────────────────────────────

    [Theory]
    [InlineData("libSkiaSharp", "libSkiaSharp.dll")]
    [InlineData("libSkiaSharp.dll", "libSkiaSharp.dll")]
    [InlineData("LIBSKIASHARP.DLL", "libSkiaSharp.dll")]
    [InlineData("libHarfBuzzSharp", "libHarfBuzzSharp.dll")]
    [InlineData("av_libGLESv2.dll", "av_libglesv2.dll")]
    [InlineData("av_libglesv2", "av_libglesv2.dll")]
    public void Each_pinned_library_name_maps_to_its_cache_file(string libraryName, string expectedFile)
    {
        // Arrange
        var cache = Path.Combine(Path.GetTempPath(), "sigil-pin-cache");

        // Act
        var fileName = NativeRuntimeBootstrap.PinnedFileNameFor(libraryName);
        var path = NativeRuntimeBootstrap.ResolvePinnedLibraryPath(cache, libraryName);

        // Assert
        fileName.Should().Be(expectedFile);
        path.Should().Be(Path.Combine(Path.GetFullPath(cache), expectedFile));
    }

    [Theory]
    [InlineData("user32.dll")]
    [InlineData("kernel32")]
    [InlineData("libSkiaSharpX")]
    [InlineData("libEGL.dll")]
    [InlineData("C:\\Downloads\\libSkiaSharp.dll")]
    [InlineData("sub/libSkiaSharp.dll")]
    [InlineData("")]
    public void A_name_that_is_not_pinned_falls_through(string libraryName)
    {
        // Arrange
        var handles = new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase)
        {
            ["libSkiaSharp.dll"] = new IntPtr(0x1000),
        };

        // Act
        var resolved = NativeRuntimeBootstrap.ResolvePinnedLibrary(libraryName, handles);

        // Assert
        NativeRuntimeBootstrap.PinnedFileNameFor(libraryName).Should().BeNull();
        resolved.Should().Be(IntPtr.Zero, "IntPtr.Zero hands the name back to the runtime's default probing");
    }

    [Fact]
    public void The_pinned_set_is_Skia_HarfBuzz_and_ANGLE()
    {
        // Arrange / Act
        var pinned = NativeRuntimeBootstrap.PinnedLibraryFileNames;

        // Assert
        pinned.Should().BeEquivalentTo("libSkiaSharp.dll", "libHarfBuzzSharp.dll", "av_libglesv2.dll");
    }

    [Fact]
    public void A_pinned_name_resolves_to_the_cache_loaded_handle()
    {
        // Arrange
        var skia = new IntPtr(0x1000);
        var angle = new IntPtr(0x2000);
        var handles = new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase)
        {
            ["libSkiaSharp.dll"] = skia,
            ["av_libglesv2.dll"] = angle,
        };

        // Act
        var resolvedSkia = NativeRuntimeBootstrap.ResolvePinnedLibrary("libSkiaSharp", handles);
        var resolvedAngle = NativeRuntimeBootstrap.ResolvePinnedLibrary("av_libGLESv2.dll", handles);

        // Assert
        resolvedSkia.Should().Be(skia);
        resolvedAngle.Should().Be(angle);
    }

    [Fact]
    public void A_pinned_name_with_no_verified_copy_is_refused_rather_than_searched()
    {
        // Arrange
        var handles = new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);

        // Act
        var act = () => NativeRuntimeBootstrap.ResolvePinnedLibrary("libHarfBuzzSharp", handles);

        // Assert
        act.Should().Throw<NativeRuntimeTrustException>()
            .WithMessage("*libHarfBuzzSharp*verified native runtime cache*");
    }

    // ── loading by full path ─────────────────────────────────────────────────

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public void The_cache_copy_is_loaded_even_with_a_same_named_dll_in_the_app_dir()
    {
        // Arrange
        using var scratch = new Scratch();
        var appDir = Directory.CreateDirectory(Path.Combine(scratch.Path, "Downloads")).FullName;
        var cacheDir = Directory.CreateDirectory(Path.Combine(scratch.Path, "cache")).FullName;
        var name = $"sigilpin_{Guid.NewGuid():N}.dll";

        // Two different, real, loadable images under the same file name: the decoy
        // beside the "exe", the genuine one in the verified cache.
        File.Copy(Path.Combine(Environment.SystemDirectory, "version.dll"), Path.Combine(appDir, name));
        File.Copy(Path.Combine(Environment.SystemDirectory, "winmm.dll"), Path.Combine(cacheDir, name));

        // The decoy is already mapped, as it would be had something loaded it first.
        var decoy = LoadLibraryExW(Path.Combine(appDir, name), IntPtr.Zero, 0);
        decoy.Should().NotBe(IntPtr.Zero);

        // Act
        var handles = NativeRuntimeBootstrap.LoadPinnedLibraries(cacheDir, new[] { name });

        // Assert
        try
        {
            handles.Should().ContainKey(name);
            handles[name].Should().NotBe(decoy, "a full-path load never adopts a same-named module from elsewhere");
            var loadedFrom = ModulePath(handles[name]);
            loadedFrom.Should().BeEquivalentTo(Path.Combine(cacheDir, name));
            loadedFrom.Should().NotStartWithEquivalentOf(appDir);
        }
        finally
        {
            foreach (var handle in handles.Values)
            {
                FreeLibrary(handle);
            }

            FreeLibrary(decoy);
        }
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public void A_library_missing_from_the_cache_is_refused_even_when_the_app_dir_has_it()
    {
        // Arrange
        using var scratch = new Scratch();
        var appDir = Directory.CreateDirectory(Path.Combine(scratch.Path, "Downloads")).FullName;
        var cacheDir = Directory.CreateDirectory(Path.Combine(scratch.Path, "cache")).FullName;
        var name = $"sigilpin_{Guid.NewGuid():N}.dll";
        File.Copy(Path.Combine(Environment.SystemDirectory, "version.dll"), Path.Combine(appDir, name));

        // Act
        var act = () => NativeRuntimeBootstrap.LoadPinnedLibraries(cacheDir, new[] { name });

        // Assert
        act.Should().Throw<NativeRuntimeTrustException>()
            .WithMessage($"*does not contain '{name}'*");
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public void A_cache_file_the_loader_rejects_is_refused()
    {
        // Arrange
        using var scratch = new Scratch();
        var cacheDir = Directory.CreateDirectory(Path.Combine(scratch.Path, "cache")).FullName;
        var name = $"sigilpin_{Guid.NewGuid():N}.dll";
        File.WriteAllBytes(Path.Combine(cacheDir, name), Encoding.ASCII.GetBytes("not a PE image"));

        // Act
        var act = () => NativeRuntimeBootstrap.LoadPinnedLibraries(cacheDir, new[] { name });

        // Assert
        act.Should().Throw<NativeRuntimeTrustException>()
            .WithMessage("*could not be loaded from the verified native runtime cache*");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    [SupportedOSPlatform("windows")]
    private static string ModulePath(IntPtr module)
    {
        var buffer = new char[32768];
        uint length;
        unsafe
        {
            fixed (char* p = buffer)
            {
                length = GetModuleFileNameW(module, p, (uint)buffer.Length);
            }
        }

        length.Should().BeGreaterThan(0u);
        return new string(buffer, 0, (int)length);
    }

    private sealed class Scratch : IDisposable
    {
        public Scratch()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "sigil-pin-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // A module still mapped by the test process; %TEMP% reclaims it.
            }
            catch (UnauthorizedAccessException)
            {
                // Same.
            }
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleFileNameW", SetLastError = true)]
    private static unsafe partial uint GetModuleFileNameW(IntPtr hModule, char* lpFilename, uint nSize);

    [LibraryImport("kernel32.dll", EntryPoint = "LoadLibraryExW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr LoadLibraryExW(string lpLibFileName, IntPtr hFile, uint dwFlags);

    [LibraryImport("kernel32.dll", EntryPoint = "FreeLibrary", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeLibrary(IntPtr hModule);
}
