using System;
using System.Runtime.Versioning;
using FluentAssertions;
using Microsoft.Win32;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Cli;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Cli;

/// <summary>
/// T12 ARP-hive parameterization: a per-user install writes HKCU only (zero HKLM
/// writes), a per-machine install targets HKLM, and the uninstall string carries
/// the scope flag so uninstall re-resolves to the same scope.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ArpScopeTests
{
    private const string UninstallRoot =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    [Fact]
    public void User_scope_registration_writes_HKCU_only_and_leaves_HKLM_untouched()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var appId = "sigil.arp.user." + Guid.NewGuid().ToString("N");
        try
        {
            ArpRegistration.Register(new ArpRegistration.Entry(
                AppId: appId,
                DisplayName: "Acme Studio",
                DisplayVersion: "3.2.0",
                Publisher: "Acme, Inc.",
                UninstallString: ArpRegistration.BuildUninstallString(@"C:\x\uninstall.exe", InstallScope.User),
                EstimatedSizeBytes: 1234),
                InstallScope.User);

            using (var hkcu = Registry.CurrentUser.OpenSubKey($@"{UninstallRoot}\{appId}"))
            {
                hkcu.Should().NotBeNull("a per-user install must write its ARP entry to HKCU");
                hkcu!.GetValue("DisplayName").Should().Be("Acme Studio");
            }

            // The acceptance bar: a per-user install performs ZERO HKLM writes.
            using var hklm = Registry.LocalMachine.OpenSubKey($@"{UninstallRoot}\{appId}");
            hklm.Should().BeNull("a per-user install must never touch HKLM");
        }
        finally
        {
#pragma warning disable CA1031
            try { ArpRegistration.Remove(appId, InstallScope.User); } catch { /* best-effort */ }
#pragma warning restore CA1031
        }
    }

    [Fact]
    public void BuildUninstallString_appends_the_scope_flag()
    {
        ArpRegistration.BuildUninstallString(@"C:\app\uninstall.exe", InstallScope.Machine)
            .Should().Be("\"C:\\app\\uninstall.exe\" /S /Uninstall /allusers");

        ArpRegistration.BuildUninstallString(@"C:\app\uninstall.exe", InstallScope.User)
            .Should().Be("\"C:\\app\\uninstall.exe\" /S /Uninstall /currentuser");
    }

    [Fact]
    public void User_scope_registration_round_trips_through_remove()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var appId = "sigil.arp.rt." + Guid.NewGuid().ToString("N");
        ArpRegistration.Register(new ArpRegistration.Entry(
            appId, "N", "1.0", "P",
            ArpRegistration.BuildUninstallString(@"C:\x\uninstall.exe", InstallScope.User), 0),
            InstallScope.User);

        Registry.CurrentUser.OpenSubKey($@"{UninstallRoot}\{appId}").Should().NotBeNull();

        ArpRegistration.Remove(appId, InstallScope.User);
        Registry.CurrentUser.OpenSubKey($@"{UninstallRoot}\{appId}")
            .Should().BeNull("Remove must delete the scope-correct ARP key");
    }
}
