using System;
using System.IO;
using System.Runtime.Versioning;
using FluentAssertions;
using Microsoft.Win32;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Cli;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Tests.Helpers;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Engine;

/// <summary>
/// P3: <see cref="InstalledStateResolver"/> reads the scope-correct ARP entry into an
/// <see cref="UpgradeState"/>. The <c>UninstallString</c> parsing is pure; the ARP
/// round-trip runs only on Windows and cleans up after itself.
/// </summary>
/// <remarks>
/// The ARP fixtures are HKCU-only with GUID-unique app ids and are removed in a
/// <c>finally</c>; nothing here writes HKLM.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class InstalledStateResolverTests
{
    private const string UninstallRoot =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

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

    /// <summary>
    /// The ARP round-trip: a planted entry maps to the right
    /// <see cref="UpgradeState"/> fields — but only where the resolver is allowed to read
    /// the hive it was planted in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The correct answer differs by host, so this branches on the OUTCOME it
    /// observed, never on an elevation reading.</strong> R2 made the resolver
    /// elevation-aware: an elevated process probes HKLM and nothing else, whatever scope
    /// it was asked for, because HKCU is writable by the unprivileged user whose
    /// <c>UninstallString</c> the elevated installer would otherwise spawn. This test
    /// plants in HKCU, so:
    /// </para>
    /// <list type="bullet">
    ///   <item>
    ///     <b>Unelevated host</b> (a developer box): HKCU is probed first, the entry is
    ///     found, and the full field mapping is asserted — version, install dir,
    ///     uninstall exe, scope.
    ///   </item>
    ///   <item>
    ///     <b>Elevated host</b> (GitHub's <c>windows-latest</c>): HKCU is not probed at
    ///     all and the resolve must come back empty. The field mapping is not exercised
    ///     there, and deliberately so — the only way to exercise it would be to plant the
    ///     entry in HKLM, i.e. write to the real machine's installed-programs list, which
    ///     this suite does not do.
    ///   </item>
    /// </list>
    /// <para>
    /// The empty arm is the one that could pass for the wrong reason — a resolver that
    /// returned <see cref="UpgradeState.None"/> unconditionally would satisfy it — so it
    /// carries an anti-vacuity guard: the host really must be elevated, established
    /// through <see cref="ObservedElevation"/>, which uses a different API from the one
    /// the resolver consults. The fixture's readability is asserted through the raw
    /// registry beforehand, independently of both.
    /// </para>
    /// </remarks>
    [WindowsFact("Windows registry")]
    public void Resolve_reads_version_install_dir_and_scope_from_the_arp_entry()
    {
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

            // The fixture really is present and readable — asserted through the raw
            // registry, so it depends on neither the resolver nor the process token.
            using (var raw = Registry.CurrentUser.OpenSubKey($@"{UninstallRoot}\{appId}"))
            {
                raw.Should().NotBeNull("the fixture must really have planted an ARP entry");
                raw!.GetValue("DisplayVersion").Should().Be("1.4.2");
            }

            // Act
            var state = InstalledStateResolver.Resolve(appId, InstallScope.User);

            // Assert — branch on what was observed, then prove the host matches it.
            if (state.Found)
            {
                ObservedElevation.IsElevated().Should().BeFalse(
                    "only an UNELEVATED process may resolve an ARP entry out of HKCU (R2); " +
                    "finding one here on an elevated host would mean the elevated " +
                    "restriction had been lost");

                state.InstalledVersion.Should().Be("1.4.2");
                state.PriorInstallDir.Should().Be(installDir);
                state.PriorUninstallExe.Should().Be(Path.Combine(installDir, "uninstall.exe"));
                state.FoundScope.Should().Be(InstallScope.User);
            }
            else
            {
                ObservedElevation.IsElevated().Should().BeTrue(
                    "the ONLY legitimate reason for a readable HKCU entry not to resolve is " +
                    "R2's elevated restriction — a resolver that returned None for any " +
                    "other reason, or a fixture that failed to plant, must fail here rather " +
                    "than be mistaken for the elevated contract");

                state.Should().Be(UpgradeState.None);
            }
        }
        finally
        {
#pragma warning disable CA1031
            try { ArpRegistration.Remove(appId, InstallScope.User); } catch { /* best-effort */ }
#pragma warning restore CA1031
        }
    }

    /// <summary>
    /// Absent on every host: nothing is planted, so neither probe order can find
    /// anything and no branching is needed.
    /// </summary>
    [WindowsFact("Windows registry")]
    public void Resolve_returns_None_when_no_entry_exists()
    {
        var appId = "sigil.p3.absent." + Guid.NewGuid().ToString("N");
        InstalledStateResolver.Resolve(appId, InstallScope.User).Found.Should().BeFalse();
    }
}
