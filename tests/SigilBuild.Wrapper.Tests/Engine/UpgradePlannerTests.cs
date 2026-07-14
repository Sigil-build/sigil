using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Engine;

/// <summary>
/// The P3 (gap G3) four-path decision table: fresh / same / upgrade / downgrade,
/// resolved by <see cref="UpgradePlanner.Decide"/> from the installed ARP state and
/// the packed version. Pure logic — no registry, no process launch.
/// </summary>
public sealed class UpgradePlannerTests
{
    private static UpgradeState Installed(string version, InstallScope scope = InstallScope.User) =>
        new(Found: true, InstalledVersion: version,
            PriorInstallDir: @"C:\Apps\Acme",
            PriorUninstallExe: @"C:\Apps\Acme\uninstall.exe",
            FoundScope: scope);

    [Fact]
    public void No_prior_install_is_fresh()
    {
        var plan = UpgradePlanner.Decide(UpgradeState.None, "1.0.0", forceDowngrade: false);

        plan.Action.Should().Be(UpgradeAction.Fresh);
        plan.InstalledVersionMalformed.Should().BeFalse();
        plan.PriorInstallDir.Should().BeEmpty();
    }

    [Fact]
    public void Same_version_is_same()
    {
        var plan = UpgradePlanner.Decide(Installed("2.1.0"), "2.1.0", forceDowngrade: false);
        plan.Action.Should().Be(UpgradeAction.Same);
    }

    [Fact]
    public void Older_installed_is_upgrade_and_carries_prior_facts()
    {
        var plan = UpgradePlanner.Decide(Installed("1.0.0"), "2.0.0", forceDowngrade: false);

        plan.Action.Should().Be(UpgradeAction.Upgrade);
        plan.InstalledVersion.Should().Be("1.0.0");
        plan.PriorInstallDir.Should().Be(@"C:\Apps\Acme");
        plan.PriorUninstallExe.Should().Be(@"C:\Apps\Acme\uninstall.exe");
        plan.RemovesPriorVersion.Should().BeTrue();
    }

    [Fact]
    public void Newer_installed_without_force_is_blocked()
    {
        var plan = UpgradePlanner.Decide(Installed("3.0.0"), "2.0.0", forceDowngrade: false);

        plan.Action.Should().Be(UpgradeAction.DowngradeBlocked);
        plan.RemovesPriorVersion.Should().BeFalse();
    }

    [Fact]
    public void Newer_installed_with_force_is_forced_downgrade()
    {
        var plan = UpgradePlanner.Decide(Installed("3.0.0"), "2.0.0", forceDowngrade: true);

        plan.Action.Should().Be(UpgradeAction.DowngradeForced);
        plan.RemovesPriorVersion.Should().BeTrue();
    }

    [Fact]
    public void Malformed_installed_version_is_treated_as_older_upgrade_with_warning()
    {
        // "1.2-beta" is not a parseable System.Version → treated as older (upgrade).
        var plan = UpgradePlanner.Decide(Installed("1.2-beta"), "1.2.0", forceDowngrade: false);

        plan.Action.Should().Be(UpgradeAction.Upgrade);
        plan.InstalledVersionMalformed.Should().BeTrue();
    }

    [Fact]
    public void Malformed_installed_version_never_blocks_even_when_ordinally_greater()
    {
        // "zzz" is ordinally greater than "1.0.0" but unparseable — must NOT block;
        // the malformed path forces an upgrade regardless of ordinal accident.
        var plan = UpgradePlanner.Decide(Installed("zzz"), "1.0.0", forceDowngrade: false);

        plan.Action.Should().Be(UpgradeAction.Upgrade);
        plan.InstalledVersionMalformed.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2.0.0-rc1")]  // SemVer packed tag — not a parseable System.Version
    public void Malformed_or_absent_packed_version_never_blocks(string? packed)
    {
        // installed is well-formed and (ordinally) greater/newer — must NOT be
        // misread as a downgrade just because the packed version is unreadable.
        var plan = UpgradePlanner.Decide(Installed("3.0.0"), packed, forceDowngrade: false);

        plan.Action.Should().Be(UpgradeAction.Upgrade);
        plan.InstalledVersionMalformed.Should().BeFalse("the installed version is well-formed");
    }

    [Fact]
    public void Identical_semver_strings_are_same_version_not_a_warned_upgrade()
    {
        var plan = UpgradePlanner.Decide(Installed("1.2.0-rc1"), "1.2.0-rc1", forceDowngrade: false);

        plan.Action.Should().Be(UpgradeAction.Same);
        plan.InstalledVersionMalformed.Should().BeFalse();
    }

    [Fact]
    public void Numerically_equal_but_differently_written_versions_are_same()
    {
        // "1.0.0" and "1.00.0" are ordinally different but numerically equal
        // (leading zeros) — the numeric-equal branch classifies them as Same.
        UpgradePlanner.Decide(Installed("1.0.0"), "1.00.0", forceDowngrade: false)
            .Action.Should().Be(UpgradeAction.Same);
    }

    [Fact]
    public void Found_scope_is_carried_into_the_plan()
    {
        var plan = UpgradePlanner.Decide(Installed("1.0.0", InstallScope.Machine), "2.0.0", forceDowngrade: false);
        plan.FoundScope.Should().Be(InstallScope.Machine);
    }
}
