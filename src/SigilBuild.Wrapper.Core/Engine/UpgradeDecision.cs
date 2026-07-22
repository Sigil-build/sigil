namespace SigilBuild.Wrapper.Engine;

using SigilBuild.Core.Manifest;

/// <summary>
/// The version-aware install paths (P3, gap G3), resolved once at session start by
/// comparing the packed version against the installed Add/Remove-Programs entry.
/// Mirrors WiX <c>MajorUpgrade</c> + downgrade-block and the Inno/NSIS
/// detect-old-and-uninstall idiom.
/// </summary>
public enum UpgradeAction
{
    /// <summary>No prior install of this app id — a normal fresh install (unchanged behaviour).</summary>
    Fresh,

    /// <summary>The same version is already installed — the existing T10 repair/reinstall path (unchanged).</summary>
    Same,

    /// <summary>
    /// An older version is installed — remove it first (run its
    /// <c>uninstall.exe /S /Uninstall</c>), then install honoring the prior install
    /// directory so user data is preserved.
    /// </summary>
    Upgrade,

    /// <summary>
    /// A newer version is installed — refuse to downgrade. The silent path exits with
    /// <see cref="InstallSession.DowngradeBlockedExitCode"/>; the wizard shows a notice screen.
    /// </summary>
    DowngradeBlocked,

    /// <summary>
    /// A newer version is installed but <c>/force-downgrade</c> was supplied — remove
    /// it first (as for an upgrade), then install the older version.
    /// </summary>
    DowngradeForced,
}

/// <summary>
/// The installed state read from the scope-correct ARP entry (P3), feeding
/// <see cref="UpgradePlanner.Decide"/>. Produced by <see cref="InstalledStateResolver"/>
/// (registry I/O) or injected in tests. <see cref="None"/> represents "no prior
/// install found".
/// </summary>
/// <param name="Found">True when an ARP entry for the app id exists in some hive.</param>
/// <param name="InstalledVersion">The entry's <c>DisplayVersion</c> (may be malformed; "" if unset).</param>
/// <param name="PriorInstallDir">
/// The directory the prior version installed into — the ARP <c>InstallLocation</c> when
/// present, else the directory of the <c>UninstallString</c> exe. Becomes the upgrade's
/// default destination. "" when it cannot be resolved.
/// </param>
/// <param name="PriorUninstallExe">
/// The prior version's <c>uninstall.exe</c> (parsed from the <c>UninstallString</c>, else
/// <c>{PriorInstallDir}\uninstall.exe</c>). Run with <c>/S /Uninstall</c> before an upgrade.
/// </param>
/// <param name="FoundScope">The hive the entry was found in — the existing install's scope.</param>
public sealed record UpgradeState(
    bool Found,
    string InstalledVersion,
    string PriorInstallDir,
    string PriorUninstallExe,
    InstallScope FoundScope)
{
    /// <summary>The "no prior install" state — drives the <see cref="UpgradeAction.Fresh"/> path.</summary>
    public static UpgradeState None { get; } =
        new(false, string.Empty, string.Empty, string.Empty, InstallScope.User);
}

/// <summary>
/// The resolved version-aware plan for a run (P3). Carries the classification plus
/// the prior-install facts the pre-body upgrade phase and the destination default need.
/// </summary>
/// <param name="Action">The chosen path (fresh / same / upgrade / downgrade-blocked / downgrade-forced).</param>
/// <param name="InstalledVersion">The installed <c>DisplayVersion</c> (for the notice / message); "" when fresh.</param>
/// <param name="PriorInstallDir">The prior install directory to honor as the default destination; "" when fresh/same.</param>
/// <param name="PriorUninstallExe">The prior <c>uninstall.exe</c> to run before an upgrade; "" when fresh/same.</param>
/// <param name="FoundScope">The existing install's scope (wins over an auto-resolved scope).</param>
/// <param name="InstalledVersionMalformed">
/// True when the installed version was not a parseable dotted version and was therefore
/// treated as older (upgrade) — the caller emits a warning.
/// </param>
public sealed record UpgradePlan(
    UpgradeAction Action,
    string InstalledVersion,
    string PriorInstallDir,
    string PriorUninstallExe,
    InstallScope FoundScope,
    bool InstalledVersionMalformed)
{
    /// <summary>The fresh-install plan (no prior install).</summary>
    public static UpgradePlan Fresh { get; } =
        new(UpgradeAction.Fresh, string.Empty, string.Empty, string.Empty, InstallScope.User, false);

    /// <summary>
    /// True when the plan removes a prior version before installing — an
    /// <see cref="UpgradeAction.Upgrade"/> or a <see cref="UpgradeAction.DowngradeForced"/>.
    /// These run the prior <c>uninstall.exe</c> as a pre-body phase and honor the prior install dir.
    /// </summary>
    public bool RemovesPriorVersion =>
        Action is UpgradeAction.Upgrade or UpgradeAction.DowngradeForced;
}
