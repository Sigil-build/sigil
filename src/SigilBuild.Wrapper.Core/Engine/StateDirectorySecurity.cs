namespace SigilBuild.Wrapper.Engine;

using System;
using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

/// <summary>
/// ACL provenance for the machine-scope install-state directory (register row R1),
/// and the shared "can only a privileged principal write here?" predicate that
/// lanes S2 and S3 gate SYSTEM-level step targets, service binaries, COM DLL loads
/// and staging directories on.
/// </summary>
/// <remarks>
/// <para>
/// <c>%ProgramData%</c> grants <c>BUILTIN\Users</c> <c>(CI)(WD,AD,WEA,WA)</c> and
/// <c>CREATOR OWNER</c> full control by inheritance, so a directory created there
/// with a bare <see cref="Directory.CreateDirectory(string)"/> is writable — and
/// owned — by whichever unprivileged user got there first.
/// <c>File.WriteAllText</c> truncates in place and preserves that owner and DACL,
/// so an elevated install/uninstall later replays records the attacker still
/// controls. <see cref="CreateHardened"/> closes the write side;
/// <see cref="IsTrusted"/> closes the read side.
/// </para>
/// <para>
/// <b>Both halves of the Windows answer are required.</b> Owner alone is not
/// enough: <c>C:\ProgramData</c> is owned by SYSTEM yet any user can create files
/// in it. DACL alone is not enough either: the owner of an object implicitly holds
/// <c>WRITE_DAC</c>, so an attacker who owns a directory can pin an admin-only DACL
/// on it, pass a DACL-only check, and re-grant themselves write at any later moment.
/// Every predicate here therefore requires a trusted owner <em>and</em> a DACL under
/// which no non-trusted principal holds a write-class right.
/// </para>
/// <para>
/// Every check fails closed: any exception (path too long, no read access, race
/// on delete) answers "not trusted" / "not admin-only" rather than propagating.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class StateDirectorySecurity
{
    /// <summary>
    /// <c>NT SERVICE\TrustedInstaller</c>. No <see cref="WellKnownSidType"/> covers it,
    /// and it <em>owns</em> <c>%WINDIR%</c>, <c>%WINDIR%\System32</c> and
    /// <c>%ProgramFiles%</c> (verified with <c>Get-Acl</c> on Windows 11). Machine-scope
    /// installs land under <c>%ProgramFiles%</c>, so excluding it would make this
    /// predicate refuse every legitimate machine install — a worse bug than the hole
    /// it closes.
    /// </summary>
    private const string TrustedInstallerSid =
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    /// <summary>
    /// Write-class rights: holding any of these on a container is enough to
    /// replace, delete or re-permission what lives in it. <c>FullControl</c> is a
    /// superset of all of them, so it needs no separate test.
    /// </summary>
    private const FileSystemRights WriteRights =
        FileSystemRights.WriteData |                     // == CreateFiles
        FileSystemRights.AppendData |                    // == CreateDirectories
        FileSystemRights.WriteAttributes |
        FileSystemRights.WriteExtendedAttributes |
        FileSystemRights.Delete |
        FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions |
        FileSystemRights.TakeOwnership;

    /// <summary>
    /// <c>GENERIC_ALL</c> | <c>GENERIC_WRITE</c>. An ACE can carry the unmapped
    /// generic bits, which no <see cref="FileSystemRights"/> member matches; treat
    /// them as write-class too rather than reading them as "no rights".
    /// </summary>
    private const int GenericWriteBits = 0x1000_0000 | 0x4000_0000;

    /// <summary>
    /// Ensure <paramref name="path"/> exists with a protected (non-inherited) DACL —
    /// SYSTEM and <c>BUILTIN\Administrators</c> FullControl, <c>BUILTIN\Users</c>
    /// ReadAndExecute — so nothing of <c>%ProgramData%</c>'s permissive inheritance
    /// survives.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When the directory already exists and does <em>not</em> pass
    /// <see cref="IsTrusted"/>, this <b>repairs</b> it: the protected admin-only DACL
    /// is re-applied over whatever was there, discarding inherited ACEs, and the
    /// repair is reported on <paramref name="progress"/>. It does not throw for that
    /// case. Repairing rather than refusing is what closes R1 on machines that
    /// already carry a pre-fix install, whose state directory is
    /// <c>BUILTIN\Administrators</c>-owned (so the old owner-only check passed) but
    /// still inherits <c>%ProgramData%</c>'s <c>BUILTIN\Users:(WD,AD)</c> grant.
    /// Only a repair that itself fails raises
    /// <see cref="UnauthorizedAccessException"/>.
    /// </para>
    /// <para>
    /// The repair then hands ownership to <c>BUILTIN\Administrators</c>, best-effort.
    /// The DACL is the load-bearing half, but the owner of an object retains implicit
    /// <c>WRITE_DAC</c> — it can re-permission the directory at will — which is why
    /// <see cref="IsTrusted"/> requires a trusted owner too. A repaired-but-still-
    /// attacker-owned directory would therefore have its state refused on every later
    /// load, turning a privilege-escalation attempt into a permanent uninstall denial
    /// for that app. Assigning an owner needs the target SID in the caller's token (or
    /// <c>SeTakeOwnership</c>/<c>SeRestorePrivilege</c>), which only an <em>elevated</em>
    /// caller has, so the attempt is swallowed and reported rather than fatal: the DACL
    /// repair stands on its own, and machine scope only ever runs elevated in production.
    /// </para>
    /// <para>
    /// In production this runs only for machine scope, which only happens elevated,
    /// so the created directory's owner is <c>BUILTIN\Administrators</c> (or SYSTEM)
    /// and the created directory does pass <see cref="IsTrusted"/>. An
    /// <em>unelevated</em> caller creates a directory it owns itself, which can never
    /// pass the strengthened predicate no matter how correct this method is — see the
    /// tests, which assert the produced DACL rather than a round trip through
    /// <see cref="IsTrusted"/>.
    /// </para>
    /// </remarks>
    public static void CreateHardened(string path, IProgress<StepProgress>? progress = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (!Directory.Exists(path))
        {
            // FileSystemAclExtensions.CreateDirectory — creates the directory WITH the
            // DACL already applied. Creating first and setting the ACL afterwards would
            // leave a window in which the inherited %ProgramData% ACEs are live.
            HardenedSecurity().CreateDirectory(path);
            return;
        }

        if (IsTrusted(path))
        {
            return;
        }

#pragma warning disable CA1031 // Any repair failure is re-thrown below as a typed, actionable error.
        try
        {
            new DirectoryInfo(path).SetAccessControl(HardenedSecurity());
        }
        catch (Exception ex)
        {
            throw new UnauthorizedAccessException(
                $"state directory '{path}' already exists, is not admin-only writable, " +
                $"and its access control list could not be repaired: {ex.Message}",
                ex);
        }
#pragma warning restore CA1031

        progress?.Report(new StepProgress(
            0,
            0,
            $"repaired the access control list of state directory '{path}' " +
            "(admin-only, inheritance disabled)",
            IsError: false));

        // The DACL repair above does not change WHO owns the directory, and an owner
        // keeps implicit WRITE_DAC — it can hand itself write access back whenever it
        // likes. IsTrusted therefore also demands a trusted owner, so leaving an
        // attacker-created directory attacker-owned would make every later TryLoad
        // refuse its state: the escalation attempt becomes a permanent uninstall
        // denial. Hand ownership to BUILTIN\Administrators so the repair is complete.
        //
        // Best-effort by necessity: only a caller whose token carries the target SID
        // (i.e. an ELEVATED one) can assign it. Machine scope only ever runs elevated
        // in production, so this succeeds where it matters; an unelevated caller keeps
        // the repaired DACL and is told the ownership fix was skipped. A failure here
        // must never fail the install — the DACL is the load-bearing half.
        var ownerRepaired = false;
#pragma warning disable CA1031 // Owner repair is best-effort: only an elevated caller can succeed.
        try
        {
            var ownership = new DirectorySecurity();
            ownership.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            new DirectoryInfo(path).SetAccessControl(ownership);
            ownerRepaired = true;
        }
        catch
        {
            // Insufficient privilege is the expected unelevated outcome; see above.
        }
#pragma warning restore CA1031

        progress?.Report(new StepProgress(
            0,
            0,
            ownerRepaired
                ? $"took ownership of state directory '{path}' for BUILTIN\\Administrators"
                : $"could not take ownership of state directory '{path}' — this needs an " +
                  "elevated caller; the admin-only ACL is in place but the directory stays " +
                  "untrusted and its state will be refused on load",
            IsError: !ownerRepaired));
    }

    /// <summary>
    /// True when <paramref name="path"/> exists, its owner is one of
    /// <c>NT AUTHORITY\SYSTEM</c>, <c>BUILTIN\Administrators</c> or
    /// <c>NT SERVICE\TrustedInstaller</c>, <em>and</em> no other principal holds a
    /// write-class right on the directory itself — i.e. only something already
    /// privileged can have authored, or can still alter, what lives in it. False on
    /// any error, including "does not exist".
    /// </summary>
    public static bool IsTrusted(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

#pragma warning disable CA1031 // Fail closed: any failure to read owner/DACL means "not trusted".
        try
        {
            return Directory.Exists(path) && IsTrustedDirectory(path);
        }
        catch
        {
            return false;
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// True when only SYSTEM, <c>BUILTIN\Administrators</c> and TrustedInstaller can
    /// write the directory <paramref name="path"/> denotes: it must be owned by one of
    /// them and no other principal may hold a write-class right on it. Used by S2 to
    /// gate SYSTEM-level step targets and by S3 to site its staging directories.
    /// False on any error.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The examined target is <paramref name="path"/> <em>itself</em> when it exists as
    /// a directory, otherwise the directory that would contain it. A directory argument
    /// must have its own ACL inspected: <c>C:\Windows\Tracing</c> carries
    /// <c>BUILTIN\Users:(RX,W)</c> while its parent <c>C:\Windows</c> is admin-only, so
    /// checking only the parent certifies a world-writable directory as safe.
    /// </para>
    /// <para>
    /// Conservative by construction: Allow ACEs are the only thing considered, so a
    /// Deny ACE never rescues a permissive Allow. Inherit-only ACEs
    /// (<see cref="PropagationFlags.InheritOnly"/>) are skipped because they grant
    /// nothing on the container itself — that is what makes the answer true for stock
    /// OS directories, every one of which carries an inherit-only
    /// <c>CREATOR OWNER:(F)</c>. A <c>CREATOR OWNER</c> ACE that <em>does</em> apply to
    /// the container counts as a non-trusted writer.
    /// </para>
    /// </remarks>
    public static bool IsAdminOnlyWritable(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

#pragma warning disable CA1031 // Fail closed: any failure to read owner/DACL means "not admin-only".
        try
        {
            var full = Path.GetFullPath(path);
            var target = Directory.Exists(full) ? full : Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(target) || !Directory.Exists(target))
            {
                return false;
            }

            return IsTrustedDirectory(target);
        }
        catch
        {
            return false;
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// The single trust decision both predicates answer with: a trusted owner AND a
    /// DACL granting no write-class right to anything else. Callers wrap it in the
    /// fail-closed <c>catch</c>; it deliberately lets exceptions out so no failure is
    /// silently read as "trusted".
    /// </summary>
    private static bool IsTrustedDirectory(string directory)
    {
        var security = new DirectoryInfo(directory)
            .GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);

        // Owner half: the owner implicitly holds WRITE_DAC, so a non-trusted owner can
        // re-permission the directory at will regardless of what the DACL says today.
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner
            || !IsTrustedPrincipal(owner))
        {
            return false;
        }

        // DACL half: an admin-OWNED directory can still be user-WRITABLE — that is
        // exactly what %ProgramData% and every pre-fix state directory under it is.
        var rules = security.GetAccessRules(
            includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));

        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow)
            {
                continue;
            }

            // Applies to children only — cannot grant anything on the container.
            if ((rule.PropagationFlags & PropagationFlags.InheritOnly) != 0)
            {
                continue;
            }

            var rights = rule.FileSystemRights;
            var writes = (rights & WriteRights) != 0 || ((int)rights & GenericWriteBits) != 0;
            if (!writes)
            {
                continue;
            }

            if (rule.IdentityReference is not SecurityIdentifier sid || !IsTrustedPrincipal(sid))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The protected admin-only DACL applied by <see cref="CreateHardened"/>, both at
    /// creation time and on the repair path.
    /// </summary>
    private static DirectorySecurity HardenedSecurity()
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        const InheritanceFlags Inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

        var security = new DirectorySecurity();

        // isProtected: true, preserveInheritance: false — DISCARD the inherited
        // %ProgramData% ACEs. Merging them would keep BUILTIN\Users' write grant
        // and CREATOR OWNER, which is the whole bug.
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        security.AddAccessRule(new FileSystemAccessRule(
            system, FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            administrators, FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            users, FileSystemRights.ReadAndExecute, Inherit, PropagationFlags.None, AccessControlType.Allow));

        // Only the DACL is ever written: the owner is left to the OS (an elevated
        // process's default owner is BUILTIN\Administrators, which IsTrusted accepts),
        // and ObjectSecurity persists only the sections it was told to modify.
        return security;
    }

    private static bool IsTrustedPrincipal(SecurityIdentifier sid) =>
        sid.IsWellKnown(WellKnownSidType.LocalSystemSid)
        || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)
        || string.Equals(sid.Value, TrustedInstallerSid, StringComparison.OrdinalIgnoreCase);
}
