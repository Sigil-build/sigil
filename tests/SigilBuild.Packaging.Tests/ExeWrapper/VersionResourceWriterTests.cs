// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using System;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Configuration;
using SigilBuild.Core.Manifest;
using SigilBuild.Packaging.ExeWrapper;
using Xunit;

namespace SigilBuild.Packaging.Tests.ExeWrapper;

/// <summary>
/// The packed installer's <c>RT_VERSION</c> resource: built from the manifest,
/// replacing the stock host's, and read back through the same
/// <see cref="FileVersionInfo"/> path Explorer's Properties → Details uses. The
/// stamping tests copy the test platform's <c>testhost.exe</c> — a real PE that
/// carries a language-neutral version resource, as the installer host does — so
/// they run on every Windows leg; the pack-level tests need the staged AOT host
/// and skip without it. (R105)
/// </summary>
public partial class VersionResourceWriterTests
{
    private static AppSection App(string version = "0.5.5", string? copyright = null) =>
        new("com.example.Dictum", "Dictum", version, "Dictum Labs", null, null, copyright);

    private static string CopyOfTestHost()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "testhost.exe");
        var copy = Path.Combine(Path.GetTempPath(), $"sigil-version-{Guid.NewGuid():N}.exe");
        File.Copy(source, copy, overwrite: true);
        return copy;
    }

    private static void DeleteQuietly(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Theory]
    [InlineData("0.5.5", 0, 5, 5, false)]
    [InlineData("1.2.3-rc.1+build.42", 1, 2, 3, true)]
    [InlineData("1.2.3+build.42", 1, 2, 3, false)]
    [InlineData("70000.65535.65536", 65535, 65535, 65535, false)]
    [InlineData("99999999999999999999999.0.1", 65535, 0, 1, false)]
    [InlineData("4.0.0-alpha", 4, 0, 0, true)]
    [InlineData("", 0, 0, 0, false)]
    public void ParseVersionQuad_takes_the_semver_core_and_clamps_each_part(
        string version, int major, int minor, int patch, bool prerelease)
    {
        // Act
        var quad = VersionResourceFields.ParseVersionQuad(version);

        // Assert
        quad.Should().Be(((ushort)major, (ushort)minor, (ushort)patch, prerelease));
    }

    [Fact]
    public void FromManifest_maps_the_app_section_and_the_produced_file_name()
    {
        // Act
        var fields = VersionResourceFields.FromManifest(
            App(copyright: "Copyright (c) 2026 Dictum Labs"), "Dictum-0.5.5-x64-Setup.exe");

        // Assert
        fields.FileDescription.Should().Be("Dictum Setup");
        fields.ProductName.Should().Be("Dictum");
        fields.CompanyName.Should().Be("Dictum Labs");
        fields.FileVersion.Should().Be("0.5.5");
        fields.ProductVersion.Should().Be("0.5.5");
        fields.OriginalFilename.Should().Be("Dictum-0.5.5-x64-Setup.exe");
        fields.InternalName.Should().Be("Dictum-0.5.5-x64-Setup.exe");
        fields.LegalCopyright.Should().Be("Copyright (c) 2026 Dictum Labs");
    }

    [Fact]
    public void FromManifest_leaves_the_copyright_empty_when_the_manifest_declares_none()
    {
        // Act
        var fields = VersionResourceFields.FromManifest(App(), "Dictum-0.5.5-x64-Setup.exe");

        // Assert
        fields.LegalCopyright.Should().BeEmpty();
    }

    [Fact]
    public void Build_emits_a_self_consistent_VS_VERSIONINFO()
    {
        // Act
        var blob = VersionResourceWriter.Build(
            VersionResourceFields.FromManifest(App("1.2.3-rc.1"), "Dictum-1.2.3-rc.1-x64-Setup.exe"));

        // Assert — root header: wLength covers the whole blob, wValueLength is
        // sizeof(VS_FIXEDFILEINFO), wType is binary, and the fixed info starts on
        // the first DWORD boundary after "VS_VERSION_INFO\0" (6 + 32 = 38 → 40).
        BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(0)).Should().Be((ushort)blob.Length);
        BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(2)).Should().Be(52);
        BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(4)).Should().Be(0);
        System.Text.Encoding.Unicode.GetString(blob, 6, 30).Should().Be("VS_VERSION_INFO");
        BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(40)).Should().Be(0xFEEF04BD);
        BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(48)).Should().Be(0x00010002, "file version MS = 1.2");
        BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(52)).Should().Be(0x00030000, "file version LS = 3.0");
        (BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(68)) & 0x2).Should().Be(0x2, "a prerelease sets VS_FF_PRERELEASE");
        (blob.Length % 4).Should().Be(0, "the last block ends on its value, which is DWORD-sized");
    }

    [Fact]
    public void Build_cuts_an_oversized_value_so_every_length_still_fits_a_WORD()
    {
        // Arrange
        var huge = new string('x', 100_000);
        var fields = VersionResourceFields.FromManifest(
            new AppSection("com.example.Big", "Big", "1.0.0", huge, null, null, huge), "Big-1.0.0-x64-Setup.exe");

        // Act
        var blob = VersionResourceWriter.Build(fields);

        // Assert
        BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(0)).Should().Be((ushort)blob.Length);
        blob.Length.Should().BeLessThan(ushort.MaxValue);
    }

    [WindowsFact]
    public async Task WriteAsync_stamps_every_field_and_the_numeric_quad()
    {
        // Arrange
        var exe = CopyOfTestHost();
        try
        {
            var fields = VersionResourceFields.FromManifest(
                App("0.5.5", "Copyright (c) 2026 Dictum Labs"), "Dictum-0.5.5-x64-Setup.exe");

            // Act
            await VersionResourceWriter.WriteAsync(exe, fields, CancellationToken.None);

            // Assert
            var info = FileVersionInfo.GetVersionInfo(exe);
            info.FileDescription.Should().Be("Dictum Setup");
            info.ProductName.Should().Be("Dictum");
            info.CompanyName.Should().Be("Dictum Labs");
            info.FileVersion.Should().Be("0.5.5");
            info.ProductVersion.Should().Be("0.5.5");
            info.OriginalFilename.Should().Be("Dictum-0.5.5-x64-Setup.exe");
            info.InternalName.Should().Be("Dictum-0.5.5-x64-Setup.exe");
            info.LegalCopyright.Should().Be("Copyright (c) 2026 Dictum Labs");
            (info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart)
                .Should().Be((0, 5, 5, 0));
            (info.ProductMajorPart, info.ProductMinorPart, info.ProductBuildPart, info.ProductPrivatePart)
                .Should().Be((0, 5, 5, 0));
            info.IsPreRelease.Should().BeFalse();
            VersionResourceWriter.ReadVersionLanguages(exe).Should().Equal(VersionResourceWriter.LanguageNeutral);
        }
        finally
        {
            DeleteQuietly(exe);
        }
    }

    [WindowsFact]
    public async Task WriteAsync_keeps_prerelease_and_build_metadata_in_the_strings_only()
    {
        // Arrange
        var exe = CopyOfTestHost();
        try
        {
            var fields = VersionResourceFields.FromManifest(
                App("1.2.3-rc.1+build.42"), "Dictum-1.2.3-rc.1+build.42-x64-Setup.exe");

            // Act
            await VersionResourceWriter.WriteAsync(exe, fields, CancellationToken.None);

            // Assert
            var info = FileVersionInfo.GetVersionInfo(exe);
            info.FileVersion.Should().Be("1.2.3-rc.1+build.42");
            info.ProductVersion.Should().Be("1.2.3-rc.1+build.42");
            (info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart)
                .Should().Be((1, 2, 3, 0));
            (info.ProductMajorPart, info.ProductMinorPart, info.ProductBuildPart, info.ProductPrivatePart)
                .Should().Be((1, 2, 3, 0));
            info.IsPreRelease.Should().BeTrue();
        }
        finally
        {
            DeleteQuietly(exe);
        }
    }

    [WindowsFact]
    public async Task WriteAsync_leaves_the_copyright_empty_and_drops_the_hosts_values()
    {
        // Arrange — testhost.exe carries Microsoft's copyright and names: after the
        // stamp, none of the host's strings may survive.
        var exe = CopyOfTestHost();
        try
        {
            FileVersionInfo.GetVersionInfo(exe).LegalCopyright.Should().NotBeNullOrEmpty(
                "the precondition is a host that asserts its own copyright");

            // Act
            await VersionResourceWriter.WriteAsync(
                exe, VersionResourceFields.FromManifest(App(), "Dictum-0.5.5-x64-Setup.exe"), CancellationToken.None);

            // Assert
            var info = FileVersionInfo.GetVersionInfo(exe);
            info.LegalCopyright.Should().BeEmpty();
            info.Comments.Should().BeEmpty();
            info.LegalTrademarks.Should().BeEmpty();
        }
        finally
        {
            DeleteQuietly(exe);
        }
    }

    [WindowsFact]
    public async Task WriteAsync_round_trips_non_ASCII_names()
    {
        // Arrange
        var exe = CopyOfTestHost();
        try
        {
            var app = new AppSection(
                "com.example.Dictum", "Диктум", "2.0.1", "ТОВ «Приклад»", null, null,
                "© 2026 ТОВ «Приклад». Усі права захищено.");
            var fields = VersionResourceFields.FromManifest(app, "Диктум-2.0.1-x64-Setup.exe");

            // Act
            await VersionResourceWriter.WriteAsync(exe, fields, CancellationToken.None);

            // Assert
            var info = FileVersionInfo.GetVersionInfo(exe);
            info.FileDescription.Should().Be("Диктум Setup");
            info.ProductName.Should().Be("Диктум");
            info.CompanyName.Should().Be("ТОВ «Приклад»");
            info.LegalCopyright.Should().Be("© 2026 ТОВ «Приклад». Усі права захищено.");
            info.OriginalFilename.Should().Be("Диктум-2.0.1-x64-Setup.exe");
        }
        finally
        {
            DeleteQuietly(exe);
        }
    }

    [WindowsFact]
    public async Task WriteAsync_replaces_a_version_resource_in_another_language_instead_of_adding_a_second()
    {
        // Arrange — give the copy a second, US-English version entry carrying the
        // host's identity, next to its language-neutral one.
        var exe = CopyOfTestHost();
        try
        {
            var stale = VersionResourceWriter.Build(new VersionResourceFields(
                "installer", "installer", "Sigil-build", "0.1.0.0", "0.1.0-alpha", "installer.dll",
                "installer.dll", "stale", 0, 1, 0, false));
            AddVersionResource(exe, language: 0x0409, stale);
            VersionResourceWriter.ReadVersionLanguages(exe).Should().Contain((ushort)0x0409);

            // Act
            await VersionResourceWriter.WriteAsync(
                exe, VersionResourceFields.FromManifest(App(), "Dictum-0.5.5-x64-Setup.exe"), CancellationToken.None);

            // Assert
            VersionResourceWriter.ReadVersionLanguages(exe).Should().Equal(VersionResourceWriter.LanguageNeutral);
            var info = FileVersionInfo.GetVersionInfo(exe);
            info.ProductName.Should().Be("Dictum");
            info.CompanyName.Should().Be("Dictum Labs");
        }
        finally
        {
            DeleteQuietly(exe);
        }
    }

    [RuntimeStagedFact]
    public async Task PackAsync_stamps_the_publishers_version_resource_on_the_full_package_and_the_web_stub()
    {
        // Arrange
        var fixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-payload");
        var load = await ManifestLoader.LoadAsync(
            Path.Combine(fixtureDir, "sigil.yaml"), new ProcessEnvironmentReader());
        load.Manifest.Should().NotBeNull();
        var manifest = load.Manifest! with
        {
            App = load.Manifest!.App with
            {
                Version = "1.2.3-rc.1+build.42",
                Copyright = "Copyright (c) 2026 Example Inc.",
            },
        };
        var outputDir = Path.Combine(Path.GetTempPath(), $"sigil-version-pack-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);
        try
        {
            var options = new PackOptions(
                SourceDirectory: Path.Combine(fixtureDir, "payload"),
                OutputDirectory: outputDir,
                Format: PackageFormat.Exe,
                Architecture: TargetArchitecture.X64,
                Payload: PayloadMode.Web,
                PackageUrl: "https://cdn.example.com/wrapapp/pkg.exe");

            // Act
            var result = await new ExeWrapperPackager().PackAsync(manifest, options, CancellationToken.None);

            // Assert
            result.Artifact.Should().NotBeNull();
            result.SecondaryArtifact.Should().NotBeNull();
            foreach (var artifact in new[] { result.Artifact!, result.SecondaryArtifact! })
            {
                var fileName = Path.GetFileName(artifact.Path);
                var info = FileVersionInfo.GetVersionInfo(artifact.Path);
                info.FileDescription.Should().Be("WrapApp Setup");
                info.ProductName.Should().Be("WrapApp");
                info.CompanyName.Should().Be("Example Inc.");
                info.FileVersion.Should().Be("1.2.3-rc.1+build.42");
                info.ProductVersion.Should().Be("1.2.3-rc.1+build.42");
                info.OriginalFilename.Should().Be(fileName);
                info.InternalName.Should().Be(fileName);
                info.LegalCopyright.Should().Be("Copyright (c) 2026 Example Inc.");
                (info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart)
                    .Should().Be((1, 2, 3, 0));
                VersionResourceWriter.ReadVersionLanguages(artifact.Path)
                    .Should().Equal(VersionResourceWriter.LanguageNeutral);
            }
        }
        finally
        {
            try { Directory.Delete(outputDir, recursive: true); } catch (IOException) { }
        }
    }

    private static void AddVersionResource(string exe, ushort language, byte[] data)
    {
        var hUpdate = BeginUpdateResourceW(exe, deleteExistingResources: false);
        if (hUpdate == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "BeginUpdateResource failed");
        var pinned = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            if (!UpdateResourceW(hUpdate, (IntPtr)16, (IntPtr)1, language, pinned.AddrOfPinnedObject(), (uint)data.Length))
            {
                var error = Marshal.GetLastWin32Error();
                _ = EndUpdateResourceW(hUpdate, fDiscard: true);
                throw new Win32Exception(error, "UpdateResource failed");
            }
            if (!EndUpdateResourceW(hUpdate, fDiscard: false))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "EndUpdateResource failed");
        }
        finally
        {
            pinned.Free();
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "BeginUpdateResourceW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr BeginUpdateResourceW(string pFileName,
        [MarshalAs(UnmanagedType.Bool)] bool deleteExistingResources);

    [LibraryImport("kernel32.dll", EntryPoint = "UpdateResourceW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateResourceW(IntPtr hUpdate, IntPtr lpType,
        IntPtr lpName, ushort wLanguage, IntPtr lpData, uint cbData);

    [LibraryImport("kernel32.dll", EntryPoint = "EndUpdateResourceW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EndUpdateResourceW(IntPtr hUpdate,
        [MarshalAs(UnmanagedType.Bool)] bool fDiscard);
}
