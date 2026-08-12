namespace SigilBuild.Wrapper.Engine;

using System;
using System.Collections.Generic;
using System.IO;

/// <summary>
/// The floor a manifest-declared out-of-tree destination must clear before it becomes an
/// anchored replay root (register row R44).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a signed declaration still needs a floor.</strong> The declaration is
/// trustworthy — it comes from the signed blob, not the journal — but "the publisher
/// meant to write here" and "everything under here may be written by a record read off
/// disk" are different claims. A manifest declaring <c>C:\</c>, <c>%ProgramData%</c> or
/// <c>%WINDIR%\System32\helper.dll</c> would, under a naive widening, hand a planted
/// journal a write primitive over a volume, over every application's shared state, or
/// over the system directory. The publisher would not even have to be malicious: one
/// over-broad <c>directory_create</c> in a shipped manifest is enough.
/// </para>
/// <para>
/// So a declaration contributes a root only when it is <em>specific</em> — a place that
/// belongs to this application rather than to Windows or to everybody. The documented
/// case R44 exists to fix, <c>C:\ProgramData\MyApp</c>, clears the floor;
/// <c>C:\ProgramData</c> itself does not.
/// </para>
/// <para>
/// <strong>Deliberately NOT part of the floor: admin-only writability.</strong> It looks
/// like the obvious hardening and it is wrong here.
/// <c>C:\ProgramData\MyApp</c> inherits <c>BUILTIN\Users:(CI)(WD,AD)</c> from its parent,
/// so requiring an admin-only ACL would refuse the exact example the documentation tells
/// publishers to use — recreating "silently unremovable" for the population this row
/// exists to serve. The residual is the same one R44's register row accepts for the
/// anchor floor: a record can be replayed inside a user-writable directory the attacker
/// already controls, which is not an escalation. The escalating consequences are closed
/// elsewhere and stay closed — see the note on <c>OwnedByThisInstall</c> below.
/// </para>
/// <para>
/// <strong>A declared root widens FILESYSTEM containment only.</strong> It is never
/// consulted by <c>ReplayAnchor.OwnedByThisInstall</c>, which governs machine
/// <c>PATH</c> entries and machine-wide execution mappings. Those stay pinned to the
/// install directory with an admin-only ACL, whatever a manifest declares. That
/// separation is the whole reason widening the anchor for R44 does not hand back the
/// hijack primitives R1 took away.
/// </para>
/// </remarks>
internal static class DeclaredRootFloor
{
    /// <summary>
    /// Vet <paramref name="declared"/>. Returns the canonical root to anchor with, or
    /// <c>null</c> — with <paramref name="rejection"/> set to the operator-facing reason.
    /// </summary>
    public static string? Vet(string? declared, out string? rejection)
    {
        rejection = null;

        var full = Canonicalize(declared);
        if (full is null)
        {
            rejection = $"'{declared}' is not a path this process can resolve";
            return null;
        }

        // A volume root anchors nothing: every path on the volume is under it.
        var volumeRoot = TryGetPathRoot(full);
        if (volumeRoot is null)
        {
            rejection = $"'{full}' has no resolvable volume root";
            return null;
        }

        if (full.Equals(Path.TrimEndingDirectorySeparator(volumeRoot), StringComparison.OrdinalIgnoreCase))
        {
            rejection =
                $"'{full}' is a volume root — anchoring to it would permit every path on the volume";
            return null;
        }

        foreach (var wellKnown in WellKnownFolders())
        {
            var normalized = Canonicalize(wellKnown);
            if (normalized is not null && full.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            {
                rejection =
                    $"'{full}' is a well-known system folder, not a location belonging to one " +
                    "application — anchoring to it would permit every record naming anything inside it";
                return null;
            }
        }

        // Inside %WINDIR% (which contains System32 and SysWOW64) a replayed write is an
        // OS-integrity question rather than an application-data one, so a declaration
        // there does not widen the anchor. A publisher who genuinely writes into the
        // Windows directory keeps today's behaviour: the record is refused, loudly, and
        // the write has to be reversed from an `uninstall:` step, which is not anchored.
        var windows = Canonicalize(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        if (windows is not null && PathContainment.IsUnder(windows, full))
        {
            rejection =
                $"'{full}' is inside the Windows directory '{windows}', where a replayed record " +
                "is an operating-system integrity question rather than an application-data one";
            return null;
        }

        // A junction anywhere from the volume root down to the declared root redirects it
        // somewhere else entirely, so the coordinate that cleared the checks above is not
        // the coordinate that would be written. Junctions need no privilege on Windows,
        // which makes this the realistic way to defeat the rest of this floor. Reuses the
        // same predicate the install-time destination guard uses (lane S2's R16 work) so
        // the two cannot drift apart.
        if (!PathContainment.IsUnderWithoutTraversal(volumeRoot, full))
        {
            rejection =
                $"'{full}' is reached through a directory junction or symbolic link, so it does " +
                "not provably name the location the manifest declared";
            return null;
        }

        return full;
    }

    /// <summary>
    /// Folders that belong to Windows or to every application at once. A declaration
    /// EQUAL to one of these is refused; a declaration INSIDE one (the documented
    /// <c>%ProgramData%\MyApp</c>) is exactly what R44 is for and passes.
    /// </summary>
    private static IEnumerable<string> WellKnownFolders()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.System);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        yield return Path.GetTempPath();
    }

    private static string? Canonicalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
#pragma warning disable CA1031 // Fail closed: an unparseable declaration anchors nothing.
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch
        {
            return null;
        }
#pragma warning restore CA1031
    }

    private static string? TryGetPathRoot(string full)
    {
#pragma warning disable CA1031 // Fail closed: a rootless path anchors nothing.
        try
        {
            var root = Path.GetPathRoot(full);
            return string.IsNullOrEmpty(root) ? null : root;
        }
        catch
        {
            return null;
        }
#pragma warning restore CA1031
    }
}
