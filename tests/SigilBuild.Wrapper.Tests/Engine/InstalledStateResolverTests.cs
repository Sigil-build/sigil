using System;
using System.IO;
using System.Runtime.Versioning;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Cli;
using SigilBuild.Wrapper.Engine;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Engine;

/// <summary>
/// P3: <see cref="InstalledStateResolver"/> reads the scope-correct ARP entry into an
/// <see cref="UpgradeState"/>. The <c>UninstallString</c> parsing is pure; the ARP
/// round-trip runs only on Windows and cleans up after itself.
/// </summary>
public sealed class InstalledStateResolverTests
{
    [Theory]
    [InlineData("\"C:\\Apps\\Acme\\uninstall.exe\" /S /Uninstall /currentuser", @"C:\Apps\Acme\uninstall.exe")]
    [InlineData("\"C:\\Program Files\\A B\\uninstall.exe\" /S /Uninstall /allusers", @"C:\Program Files\A B\uninstall.exe")]
    [InlineData("C:\\Apps\\Acme\\uninstall.exe /S", @"C:\Apps\Acme\uninstall.exe")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void ParseUninstallExe_extracts_the_exe_path(string uninstallString, string expected)
    {
        InstalledStateResolver.ParseUninstallExe(uninstallString).Should().Be(expected);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Resolve_reads_version_install_dir_and_scope_from_the_arp_entry()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var appId = "sigil.p3.resolve." + Guid.NewGuid().ToString("N");
        var installDir = Path.Combine(Path.GetTempPath(), appId);
        try
        {
            ArpRegistration.Register(new ArpRegistration.Entry(
                AppId: appId,
                DisplayName: "Acme Studio",
                DisplayVersion: "1.4.2",
                Publisher: "Acme, Inc.",
                UninstallString: ArpRegistration.BuildUninstallString(
                    Path.Combine(installDir, "uninstall.exe"), InstallScope.User),
                EstimatedSizeBytes: 0,
                InstallLocation: installDir),
                InstallScope.User);

            var state = InstalledStateResolver.Resolve(appId, InstallScope.User);

            state.Found.Should().BeTrue();
            state.InstalledVersion.Should().Be("1.4.2");
            state.PriorInstallDir.Should().Be(installDir);
            state.PriorUninstallExe.Should().Be(Path.Combine(installDir, "uninstall.exe"));
            state.FoundScope.Should().Be(InstallScope.User);
        }
        finally
        {
#pragma warning disable CA1031
            try { ArpRegistration.Remove(appId, InstallScope.User); } catch { /* best-effort */ }
#pragma warning restore CA1031
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Resolve_returns_None_when_no_entry_exists()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var appId = "sigil.p3.absent." + Guid.NewGuid().ToString("N");
        InstalledStateResolver.Resolve(appId, InstallScope.User).Found.Should().BeFalse();
    }
}
