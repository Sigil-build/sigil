using System;
using System.IO;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Packaging.ExeWrapper;
using Xunit;

namespace SigilBuild.Packaging.Tests.ExeWrapper;

/// <summary>
/// Unit tests for <see cref="WrapperRuntimeLocator"/> — the per-architecture
/// resolution of the staged Native-AOT host runtime (spec T3). These use a fake
/// staged <c>runtimes/&lt;rid&gt;/</c> directory and never require a real AOT binary.
/// </summary>
public class WrapperRuntimeLocatorTests
{
    [Theory]
    [InlineData(TargetArchitecture.X64, "win-x64")]
    [InlineData(TargetArchitecture.Arm64, "win-arm64")]
    public void Locate_resolves_the_per_rid_runtime_path(TargetArchitecture arch, string rid)
    {
        using var staged = new StagedRuntimes();
        var expected = staged.CreateFakeRuntime(rid);

        var resolved = WrapperRuntimeLocator.Locate(arch, staged.Root);

        resolved.Should().Be(expected);
        Path.GetFileName(resolved).Should().Be("SigilBuild.Installer.Host.exe");
        resolved.Should().Contain(Path.Combine("runtimes", rid));
    }

    [Fact]
    public void Locate_selects_the_matching_rid_when_both_are_staged()
    {
        using var staged = new StagedRuntimes();
        var x64 = staged.CreateFakeRuntime("win-x64");
        var arm64 = staged.CreateFakeRuntime("win-arm64");

        WrapperRuntimeLocator.Locate(TargetArchitecture.X64, staged.Root).Should().Be(x64);
        WrapperRuntimeLocator.Locate(TargetArchitecture.Arm64, staged.Root).Should().Be(arm64);
    }

    [Fact]
    public void Locate_throws_FileNotFound_when_the_runtime_is_not_staged()
    {
        using var staged = new StagedRuntimes();
        // win-arm64 present, win-x64 absent.
        staged.CreateFakeRuntime("win-arm64");

        var act = () => WrapperRuntimeLocator.Locate(TargetArchitecture.X64, staged.Root);

        act.Should().Throw<FileNotFoundException>()
            .Which.Message.Should().Contain("win-x64");
    }

    [Theory]
    [InlineData(TargetArchitecture.X64, "win-x64")]
    [InlineData(TargetArchitecture.Arm64, "win-arm64")]
    public void RidFor_maps_each_architecture(TargetArchitecture arch, string rid)
    {
        WrapperRuntimeLocator.RidFor(arch).Should().Be(rid);
    }

    private sealed class StagedRuntimes : IDisposable
    {
        public string Root { get; } = Path.Combine(
            Path.GetTempPath(), $"sigil-locator-{Guid.NewGuid():N}");

        public string CreateFakeRuntime(string rid)
        {
            var dir = Path.Combine(Root, "runtimes", rid);
            Directory.CreateDirectory(dir);
            var exe = Path.Combine(dir, "SigilBuild.Installer.Host.exe");
            File.WriteAllBytes(exe, new byte[] { 0x4D, 0x5A }); // "MZ" — a stand-in stub.
            return exe;
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { /* best effort */ }
        }
    }
}
