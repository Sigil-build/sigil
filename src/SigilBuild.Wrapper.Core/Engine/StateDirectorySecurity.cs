namespace SigilBuild.Wrapper.Engine;

using System;
using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

/// <summary>
/// ACL provenance for the machine-scope install-state directory (register row R1).
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
/// Every check fails closed: any exception (path too long, no read access, race
/// on delete) answers "not trusted" / "not admin-only" rather than propagating.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class StateDirectorySecurity
{
    /// <summary>
    /// <c>NT SERVICE\TrustedInstaller</c>. No <see cref="WellKnownSidType"/> covers it,
    /// and it owns/holds full control over <c>%WINDIR%</c> and <c>%ProgramFiles%</c>,
    /// so it must count as a trusted writer or every OS directory reads as unsafe.
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
    /// Create <paramref name="path"/> with an explicit, non-inherited DACL —
    /// SYSTEM and <c>BUILTIN\Administrators</c> FullControl, <c>BUILTIN\Users</c>
    /// read — so nothing of <c>%ProgramData%</c>'s permissive inheritance survives.
    /// No-op when the directory already exists and passes <see cref="IsTrusted"/>;
    /// throws <see cref="UnauthorizedAccessException"/> when it exists and does not,
    /// because that is exactly the pre-created directory R1 describes.
    /// </summary>
    public static void CreateHardened(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (Directory.Exists(path))
        {
            if (!IsTrusted(path))
            {
                throw new UnauthorizedAccessException(
                    $"state directory '{path}' already exists and is not owned by SYSTEM " +
                    "or Administrators; refusing to use it");
            }

            return;
        }

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

        // The owner is deliberately left to the OS: an elevated process's default
        // owner is BUILTIN\Administrators, which IsTrusted accepts. Setting it
        // explicitly would need SeRestorePrivilege on some tokens and would turn a
        // hardening step into an install failure.
        // FileSystemAclExtensions.CreateDirectory — creates the directory WITH the
        // DACL already applied. Creating first and setting the ACL afterwards would
        // leave a window in which the inherited %ProgramData% ACEs are live.
        security.CreateDirectory(path);
    }

    /// <summary>
    /// True when <paramref name="path"/> exists and its owner is
    /// <c>NT AUTHORITY\SYSTEM</c> or <c>BUILTIN\Administrators</c> — i.e. it can
    /// only have been authored by something already privileged. False on any
    /// error, including "does not exist".
    /// </summary>
    public static bool IsTrusted(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

#pragma warning disable CA1031 // Fail closed: any failure to read the owner means "not trusted".
        try
        {
            if (!Directory.Exists(path))
            {
                return false;
            }

            var owner = new DirectoryInfo(path)
                .GetAccessControl(AccessControlSections.Owner)
                .GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;

            return owner is not null
                && (owner.IsWellKnown(WellKnownSidType.LocalSystemSid)
                    || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid));
        }
        catch
        {
            return false;
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// True when only SYSTEM, <c>BUILTIN\Administrators</c> and TrustedInstaller
    /// hold write-class rights on the directory <em>containing</em>
    /// <paramref name="path"/> — the container is what decides whether an
    /// unprivileged process can rename, delete or replace <paramref name="path"/>
    /// itself. Used by S2 to gate SYSTEM-level step targets and by S3 to site its
    /// staging directories. False on any error.
    /// </summary>
    /// <remarks>
    /// Conservative by construction: Allow ACEs are the only thing considered, so
    /// a Deny ACE never rescues a permissive Allow. Inherit-only ACEs (
    /// <see cref="PropagationFlags.InheritOnly"/>) are skipped because they grant
    /// nothing on the container itself — that is what makes the answer true for
    /// stock OS directories, every one of which carries an inherit-only
    /// <c>CREATOR OWNER:(F)</c>. A <c>CREATOR OWNER</c> ACE that <em>does</em>
    /// apply to the container counts as a non-admin writer.
    /// </remarks>
    public static bool IsAdminOnlyWritable(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

#pragma warning disable CA1031 // Fail closed: any failure to read the DACL means "not admin-only".
        try
        {
            var container = Path.GetDirectoryName(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(container) || !Directory.Exists(container))
            {
                return false;
            }

            var rules = new DirectoryInfo(container)
                .GetAccessControl(AccessControlSections.Access)
                .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));

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

                if (rule.IdentityReference is not SecurityIdentifier sid || !IsPrivileged(sid))
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
#pragma warning restore CA1031
    }

    private static bool IsPrivileged(SecurityIdentifier sid) =>
        sid.IsWellKnown(WellKnownSidType.LocalSystemSid)
        || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)
        || string.Equals(sid.Value, TrustedInstallerSid, StringComparison.OrdinalIgnoreCase);
}
