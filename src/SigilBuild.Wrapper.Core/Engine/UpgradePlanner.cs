namespace SigilBuild.Wrapper.Engine;

using System;

/// <summary>
/// The pure decision function for version-aware installs (P3, gap G3): classify a
/// run as fresh / same / upgrade / downgrade from the installed ARP state and the
/// packed version, honoring <c>/force-downgrade</c>. Contains no I/O — the registry
/// read is done by <see cref="InstalledStateResolver"/> — so the four-path decision
/// table is fully unit-testable.
/// </summary>
public static class UpgradePlanner
{
    /// <summary>
    /// Decide the version-aware plan.
    /// <list type="bullet">
    ///   <item><description>no prior install → <see cref="UpgradeAction.Fresh"/>;</description></item>
    ///   <item><description>installed == packed → <see cref="UpgradeAction.Same"/>;</description></item>
    ///   <item><description>installed &lt; packed → <see cref="UpgradeAction.Upgrade"/>;</description></item>
    ///   <item><description>installed &gt; packed → <see cref="UpgradeAction.DowngradeBlocked"/>,
    ///   or <see cref="UpgradeAction.DowngradeForced"/> when <paramref name="forceDowngrade"/>;</description></item>
    ///   <item><description>installed version malformed → treated as older → <see cref="UpgradeAction.Upgrade"/>
    ///   with <see cref="UpgradePlan.InstalledVersionMalformed"/> set (warn).</description></item>
    /// </list>
    /// Version ordering uses the same <see cref="VersionComparison"/> semantics as the
    /// <c>version_gte(...)</c> expression function.
    /// </summary>
    public static UpgradePlan Decide(UpgradeState state, string? packedVersion, bool forceDowngrade)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (!state.Found)
        {
            return UpgradePlan.Fresh;
        }

        var installed = state.InstalledVersion;
        var packed = packedVersion ?? string.Empty;

        // An exact string match — including two identical non-numeric / SemVer
        // strings — is a same-version reinstall (the repair path), never a warn.
        var sameVersion = installed.Length > 0
            && string.Equals(installed, packed, StringComparison.Ordinal);

        // A malformed installed version — not a parseable dotted version, which
        // includes SemVer pre-release tags System.Version can't read — is treated as
        // OLDER than the packed build: upgrade over it and warn. This is the flag the
        // caller warns on; it is only meaningful when the versions are not identical.
        var installedMalformed = !sameVersion && !VersionComparison.IsWellFormed(installed);

        UpgradeAction action;
        if (sameVersion)
        {
            action = UpgradeAction.Same;
        }
        else if (installedMalformed || !VersionComparison.IsWellFormed(packed))
        {
            // Either side is not a well-formed numeric version, so a numeric ordering
            // can't be proven. Never BLOCK on that uncertainty — an absent/malformed
            // packed version (e.g. a SemVer tag, or an un-set blob version) must not
            // refuse the install. Treat it as an upgrade: the same safe "remove old,
            // install new" direction used for a malformed installed version.
            action = UpgradeAction.Upgrade;
        }
        else
        {
            action = VersionComparison.Compare(installed, packed) switch
            {
                < 0 => UpgradeAction.Upgrade,            // installed older than packed
                0 => UpgradeAction.Same,                 // numerically equal (e.g. 1.0 vs 1.0.0)
                _ => forceDowngrade                      // installed newer than packed
                    ? UpgradeAction.DowngradeForced
                    : UpgradeAction.DowngradeBlocked,
            };
        }

        return new UpgradePlan(
            action,
            installed,
            state.PriorInstallDir,
            state.PriorUninstallExe,
            state.FoundScope,
            installedMalformed);
    }
}
