namespace SigilBuild.Wrapper.Engine;

using System;
using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

/// <summary>
/// A private, freshly-named directory to download an executable into, plus the
/// verify-and-hold primitive that closes the verify-&gt;launch gap of register row
/// R12 (and is the shape R5 needs): <see cref="OpenVerified"/> re-hashes the staged
/// file <em>from an open handle</em> whose sharing mode denies write and delete, and
/// hands that handle back so the caller keeps it open across
/// <c>Process.Start</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>What was wrong.</b> <c>PrerequisiteRunner</c> and <c>UpdateRunner</c> both
/// downloaded to <c>%TEMP%\…-{Guid}.exe</c>, verified the SHA-256 <em>inside</em> the
/// downloader, closed the file, and only then launched it. The GUID name blocks
/// pre-planting, but the file is created with <c>%TEMP%</c>'s default ACLs and
/// <b>no handle is held</b> between the verify and the launch — so any process
/// running as the same user (the normal split-token-admin case) that watches the
/// directory can replace the bytes in that window and have them executed, elevated.
/// </para>
/// <para>
/// <b>Two details are the whole point.</b>
/// </para>
/// <list type="number">
///   <item>
///     <description>
///     The handle is opened with <see cref="FileShare.Read"/>. <b>Not</b>
///     <see cref="FileShare.None"/>: Windows checks <c>CreateProcess</c>'s image open
///     (<c>FILE_READ_DATA | FILE_EXECUTE</c>) against the share mode of every existing
///     handle, so <c>FileShare.None</c> would make the launch itself fail with a
///     sharing violation and break the very thing being protected. <b>Not</b>
///     <see cref="FileShare.ReadWrite"/> either, which permits exactly the overwrite
///     being closed. <see cref="FileShare.Read"/> admits readers and the loader while
///     denying <c>FILE_WRITE_DATA</c> and <c>DELETE</c> — the same mechanism that makes
///     a running .exe unwritable.
///     </description>
///   </item>
///   <item>
///     <description>
///     The re-hash reads the <see cref="FileStream"/> that was just opened, never the
///     path. Re-hashing by path opens a second handle and re-introduces the identical
///     race: the bytes hashed would not provably be the bytes the surviving handle —
///     and therefore the loader — sees.
///     </description>
///   </item>
/// </list>
/// <para>
/// <b>Where it stages.</b> When the process is elevated the directory is sited under
/// an admin-only root (<c>%ProgramData%\Sigil\staging</c>, created and repaired by
/// <see cref="StateDirectorySecurity.CreateHardened"/> and then <em>confirmed</em>
/// with <see cref="StateDirectorySecurity.IsAdminOnlyWritable"/> — the one shared
/// "who can write here?" predicate; this type deliberately implements no second ACL
/// check). Unelevated there is no admin-only location to reach, so it falls back to
/// the caller's root (or <c>%TEMP%</c>) and still applies a <em>protected</em>
/// (inheritance-discarding) DACL granting only the current user, SYSTEM and
/// <c>BUILTIN\Administrators</c>. That is weaker by construction — an unelevated
/// process cannot create something it cannot itself modify — which is why
/// <see cref="OpenVerified"/>, not the directory ACL, is the load-bearing half.
/// </para>
/// </remarks>
internal sealed class SecureStaging : IDisposable
{
    /// <summary><c>%ProgramData%\Sigil\staging</c>, the elevated staging root.</summary>
    private const string SigilFolder = "Sigil";
    private const string StagingFolder = "staging";

    private bool _disposed;

    private SecureStaging(string directory, bool isAdminOnly)
    {
        Directory = directory;
        IsAdminOnly = isAdminOnly;
    }

    /// <summary>The freshly created, per-run staging directory. Never reused.</summary>
    public string Directory { get; }

    /// <summary>
    /// True when the staging directory sits under a root that
    /// <see cref="StateDirectorySecurity.IsAdminOnlyWritable"/> accepted — i.e. only
    /// SYSTEM, <c>BUILTIN\Administrators</c> or TrustedInstaller can write it. Only an
    /// elevated process can reach that state; unelevated runs report <c>false</c>
    /// rather than pretending otherwise.
    /// </summary>
    public bool IsAdminOnly { get; }

    /// <summary>
    /// Create a private staging directory named <c>sigil-{purpose}-{guid}</c> under an
    /// admin-only root when elevated, otherwise under <paramref name="fallbackRoot"/>
    /// (default <c>%TEMP%</c>), with a protected — non-inherited — DACL.
    /// </summary>
    /// <param name="purpose">Short tag for the directory name, e.g. <c>prereq</c>.</param>
    /// <param name="fallbackRoot">
    /// Root to use when no admin-only root is available. Lets a caller that already
    /// owns a session temp directory keep staging inside it; <c>null</c> means
    /// <see cref="Path.GetTempPath"/>.
    /// </param>
    public static SecureStaging Create(string purpose, string? fallbackRoot = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(purpose);

        var (root, isAdminOnly) = ResolveRoot(fallbackRoot);
        var directory = Path.Combine(root, $"sigil-{Sanitize(purpose)}-{Guid.NewGuid():N}");

        if (OperatingSystem.IsWindows())
        {
            CreatePrivateDirectory(directory, isAdminOnly);
        }
        else
        {
            // Off Windows there are no ACLs to apply; the wrapper only ships on
            // Windows, but this assembly is built and unit-tested cross-platform.
            System.IO.Directory.CreateDirectory(directory);
        }

        return new SecureStaging(directory, isAdminOnly);
    }

    /// <summary>
    /// The full path of <paramref name="fileName"/> inside this staging directory.
    /// <paramref name="fileName"/> must be a bare file name — a separator or a rooted
    /// path would let a caller stage outside the private directory, which is the whole
    /// guarantee.
    /// </summary>
    public string PathFor(string fileName)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        if (!string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"'{fileName}' must be a bare file name — a staged file cannot live outside its staging directory",
                nameof(fileName));
        }
        return Path.Combine(Directory, fileName);
    }

    /// <summary>
    /// Open <paramref name="fileName"/> with <see cref="FileShare.Read"/> (denying
    /// write and delete), re-verify its SHA-256 <em>from that handle</em> against
    /// <paramref name="expectedSha256"/>, and return the still-open handle positioned
    /// at 0. The caller must keep it open across <c>Process.Start</c>: for as long as
    /// it lives, the bytes that were hashed are the bytes the loader will map.
    /// </summary>
    /// <exception cref="StagedFileVerificationException">
    /// The file's bytes no longer hash to <paramref name="expectedSha256"/> — it was
    /// replaced after it was verified. That is the TOCTOU being closed, so it is a
    /// hard failure, never a warning.
    /// </exception>
    public FileStream OpenVerified(string fileName, string expectedSha256)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var expected = (expectedSha256 ?? string.Empty).Trim();
        if (expected.Length == 0)
        {
            throw new StagedFileVerificationException(
                $"staged file '{fileName}' cannot be verified: no expected sha256 was supplied");
        }

        var path = PathFor(fileName);

        // FileShare.Read, not None (which would fail CreateProcess) and not ReadWrite
        // (which would permit the swap). See the class remarks.
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var verified = false;
        try
        {
            // Hashed FROM THE HANDLE. Hashing by path would open a second handle and
            // reintroduce the race this method exists to close.
            var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new StagedFileVerificationException(
                    $"staged file '{fileName}' no longer matches its verified sha256 " +
                    $"(expected {expected}, got {actual}) — it was replaced after verification; refusing to run it");
            }

            stream.Seek(0, SeekOrigin.Begin);
            verified = true;
            return stream;
        }
        finally
        {
            if (!verified)
            {
                stream.Dispose();
            }
        }
    }

    /// <summary>Remove the staging directory and everything in it. Best-effort.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

#pragma warning disable CA1031 // Best-effort cleanup of a temp staging directory; a leftover must never fail an install.
        try
        {
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
        }
        catch
        {
            // A file still held open (e.g. a launched child) leaves the directory
            // behind; the OS temp sweeper reclaims it.
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// The staging root: an admin-only one when this process is elevated and one can
    /// be established, otherwise the caller's fallback (or <c>%TEMP%</c>). The bool is
    /// whether the returned root passed
    /// <see cref="StateDirectorySecurity.IsAdminOnlyWritable"/>.
    /// </summary>
    private static (string Root, bool IsAdminOnly) ResolveRoot(string? fallbackRoot)
    {
        if (OperatingSystem.IsWindows() && Elevation.IsProcessElevated())
        {
            var adminRoot = TryCreateAdminOnlyRoot();
            if (adminRoot is not null)
            {
                return (adminRoot, true);
            }
        }

        var root = string.IsNullOrWhiteSpace(fallbackRoot) ? Path.GetTempPath() : fallbackRoot!;
        System.IO.Directory.CreateDirectory(root);
        return (root, false);
    }

    /// <summary>
    /// Establish <c>%ProgramData%\Sigil\staging</c> as an admin-only root, or return
    /// <c>null</c> if that cannot be confirmed. Both levels are hardened and both are
    /// checked: an attacker who owned the intermediate <c>%ProgramData%\Sigil</c> could
    /// delete and re-create <c>staging</c> underneath an otherwise correct check.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static string? TryCreateAdminOnlyRoot()
    {
#pragma warning disable CA1031 // Fail soft to %TEMP%: an unusable admin root must not break the install, and OpenVerified is the load-bearing half either way.
        try
        {
            var sigil = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), SigilFolder);
            var staging = Path.Combine(sigil, StagingFolder);

            StateDirectorySecurity.CreateHardened(sigil);
            StateDirectorySecurity.CreateHardened(staging);

            // The frozen cross-lane predicate (S1) is the ONLY "who can write here?"
            // answer used anywhere in the engine — deliberately not re-implemented.
            return StateDirectorySecurity.IsAdminOnlyWritable(sigil)
                && StateDirectorySecurity.IsAdminOnlyWritable(staging)
                ? staging
                : null;
        }
        catch
        {
            return null;
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Create the per-run directory with a protected DACL — inheritance discarded, so
    /// nothing of the parent's permissions survives.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void CreatePrivateDirectory(string directory, bool adminOnly)
    {
        if (adminOnly)
        {
            // Elevated: SYSTEM + Administrators full control, Users read-execute. The
            // elevated caller is itself a member of BUILTIN\Administrators, so it needs
            // no ACE of its own — adding one would make the directory non-admin-only.
            StateDirectorySecurity.CreateHardened(directory);
            return;
        }

        // Unelevated: the strongest honest DACL is "this user, SYSTEM and admins".
        // %TEMP% is already per-user, but the protected DACL discards whatever the
        // parent inherited (a redirected or pre-existing TEMP may grant more).
        PrivateSecurity().CreateDirectory(directory);
    }

    [SupportedOSPlatform("windows")]
    private static DirectorySecurity PrivateSecurity()
    {
        const InheritanceFlags Inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));

        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User is SecurityIdentifier user)
        {
            security.AddAccessRule(new FileSystemAccessRule(
                user, FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
        }

        return security;
    }

    private static string Sanitize(string purpose)
    {
        Span<char> buffer = stackalloc char[Math.Min(purpose.Length, 32)];
        var n = 0;
        foreach (var c in purpose)
        {
            if (n == buffer.Length)
            {
                break;
            }
            buffer[n++] = char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-';
        }
        return n == 0 ? "staging" : new string(buffer[..n]);
    }
}

/// <summary>
/// The staged file's bytes did not (or could not) be confirmed against the SHA-256
/// they were downloaded under. Distinct from an <see cref="IOException"/> so a caller
/// can tell "it was swapped" from "it could not be opened" — both refuse the launch,
/// but only one is an attack signature.
/// </summary>
internal sealed class StagedFileVerificationException : Exception
{
    public StagedFileVerificationException(string message)
        : base(message)
    {
    }

    public StagedFileVerificationException()
    {
    }

    public StagedFileVerificationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
