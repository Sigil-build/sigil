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
/// an admin-only root (<c>%ProgramData%\Sigil\staging</c>, created hardened and then
/// <em>confirmed</em> with <see cref="StateDirectorySecurity.IsAdminOnlyWritable"/> —
/// the one shared "who can write here?" predicate; this type deliberately implements
/// no second ACL check). Unelevated there is no admin-only location to reach, so it
/// falls back to the caller's root (or <c>%TEMP%</c>) and still applies a
/// <em>protected</em> (inheritance-discarding) DACL granting only the current user,
/// SYSTEM and <c>BUILTIN\Administrators</c>. That is weaker by construction — an
/// unelevated process cannot create something it cannot itself modify — which is why
/// <see cref="OpenVerified"/>, not the directory ACL, is the load-bearing half.
/// </para>
/// <para>
/// <b>An elevated run that cannot get its admin-only root refuses.</b> It does not
/// stage in a user-writable directory and carry on. The reason is announced on the
/// <c>report</c> callback first — exception type and message included — and the same
/// text is carried by the thrown <see cref="StagingSecurityException"/>, so the cause
/// survives even where no sink is attached.
/// </para>
/// <para>
/// That is a policy decision, and the argument for it is that the alternative is not
/// checkable. A degraded elevated run is <em>survivable</em> as long as every caller
/// holds the <see cref="OpenVerified"/> handle across the launch — but that is a
/// per-call-site invariant, invisible at this type's boundary and easy for the next
/// consumer to miss. Register row R5's own stub is exactly such a consumer: two
/// independent steps with a gap between them. Combined with a download that wrote with
/// <c>FileMode.Create</c>, an attacker who forced the degrade and won the create race
/// got a write through a planted hardlink or reparse point from an elevated process.
/// Both halves are closed here: this refuses, and <c>SigilDownloader</c> removes the
/// destination name before creating it. The cost is a denial of service — a
/// non-administrator who squats <c>%ProgramData%\Sigil</c> can stop an elevated run
/// staging a download — which is the better of the two failures by a wide margin.
/// </para>
/// <para>
/// <b>Unelevated is not a degrade.</b> There is no admin-only location an unelevated
/// process can reach, so <c>%TEMP%</c> is the only option there is, not a downgrade; it
/// is taken silently and <see cref="OpenVerified"/> carries the guarantee.
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
    /// <param name="report">
    /// Receives <c>(message, isError)</c> lines. An <em>elevated</em> run that cannot
    /// establish its admin-only root reports why here as an error and then <b>refuses</b>
    /// — it never stages in a user-writable directory instead. An elevated process
    /// quietly downgrading its own containment is the failure mode that reads as success.
    /// <para>
    /// <b>Required, and deliberately not defaulted.</b> An optional reporting parameter
    /// reintroduces that exact failure by omission — a future call site that simply does
    /// not pass one loses the degrade line with no compile error to catch it. Making it
    /// required means the omission cannot be made by accident; passing a sink that
    /// discards has to be a decision someone writes down.
    /// </para>
    /// </param>
    /// <param name="fallbackRoot">
    /// Root to use when no admin-only root is available. Lets a caller that already
    /// owns a session temp directory keep staging inside it; <c>null</c> means
    /// <see cref="Path.GetTempPath"/>.
    /// </param>
    public static SecureStaging Create(
        string purpose, Action<string, bool> report, string? fallbackRoot = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(purpose);
        ArgumentNullException.ThrowIfNull(report);

        var (root, isAdminOnly) = ResolveRoot(fallbackRoot, report);
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
        return OpenVerified(PathFor(fileName), expectedSha256, fileName);
    }

    /// <summary>
    /// The verify-and-hold primitive over an arbitrary absolute path, for a caller that
    /// did not stage the file through a <see cref="SecureStaging"/> instance — register
    /// row R5's <c>http_download</c> → <c>run_program</c> pair, where the download step
    /// chose the destination and the run step must re-confirm it. Identical guarantees:
    /// <see cref="FileShare.Read"/>, and the hash taken from the returned handle rather
    /// than from the path.
    /// </summary>
    /// <exception cref="StagedFileVerificationException">
    /// The bytes no longer hash to <paramref name="expectedSha256"/>.
    /// </exception>
    public static FileStream OpenVerifiedFile(string path, string expectedSha256) =>
        OpenVerified(path, expectedSha256, Path.GetFileName(path));

    private static FileStream OpenVerified(string path, string expectedSha256, string displayName)
    {
        var expected = (expectedSha256 ?? string.Empty).Trim();
        if (expected.Length == 0)
        {
            throw new StagedFileVerificationException(
                $"staged file '{displayName}' cannot be verified: no expected sha256 was supplied");
        }

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
                    $"staged file '{displayName}' no longer matches its verified sha256 " +
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
    /// The staging root: an admin-only one when this process is elevated, otherwise the
    /// caller's fallback (or <c>%TEMP%</c>). The bool is whether the returned root
    /// passed <see cref="StateDirectorySecurity.IsAdminOnlyWritable"/>.
    /// </summary>
    private static (string Root, bool IsAdminOnly) ResolveRoot(string? fallbackRoot, Action<string, bool> report) =>
        ResolveRoot(
            fallbackRoot,
            report,
            elevated: OperatingSystem.IsWindows() && Elevation.IsProcessElevated(),
            commonAppData: Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));

    /// <summary>
    /// The decision itself, with the two facts it depends on passed in so the elevated
    /// branch is reachable from an unelevated test process — the only way to assert the
    /// refusal on a developer box or an unelevated runner.
    /// </summary>
    /// <exception cref="StagingSecurityException">
    /// This process is elevated and no administrator-only root could be established.
    /// See the type remarks for why that is a refusal rather than a fallback.
    /// </exception>
    internal static (string Root, bool IsAdminOnly) ResolveRoot(
        string? fallbackRoot, Action<string, bool> report, bool elevated, string commonAppData)
    {
        // Only an ELEVATED run can reach an admin-only root, and only an elevated run is
        // giving anything up by not having one: staging in %TEMP% unelevated is the only
        // option there is, not a downgrade, and must not cry wolf.
        if (elevated)
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new StagingSecurityException(
                    "an administrator-only staging directory is a Windows-only concept, so this elevated " +
                    "run cannot stage a downloaded executable safely");
            }

            // The reason is both reported and carried by the exception: a call site with
            // no progress sink attached must still be able to say what went wrong.
            string? cause = null;
            var adminRoot = TryResolveAdminOnlyRoot(
                commonAppData,
                (message, isError) =>
                {
                    cause = message;
                    report(message, isError);
                });
            if (adminRoot is not null)
            {
                return (adminRoot, true);
            }

            throw new StagingSecurityException(
                cause ?? "this elevated process could not establish an administrator-only staging directory");
        }

        var root = string.IsNullOrWhiteSpace(fallbackRoot) ? Path.GetTempPath() : fallbackRoot!;
        System.IO.Directory.CreateDirectory(root);
        return (root, false);
    }

    /// <summary>
    /// Establish <c>&lt;commonAppData&gt;\Sigil\staging</c> as an admin-only root, or
    /// return <c>null</c> having <b>reported why</b>. Both levels are checked: an
    /// attacker who owned the intermediate <c>Sigil</c> directory could delete and
    /// re-create <c>staging</c> underneath an otherwise correct check.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>%ProgramData%\Sigil</c> is lane S1's install-state root and is NOT repaired
    /// from here.</b> It is created when it is simply missing, but an existing one that
    /// fails <see cref="StateDirectorySecurity.IsAdminOnlyWritable"/> is left exactly as
    /// found: re-permissioning it, or taking ownership of it, as a side effect of
    /// staging a downloaded prerequisite is far too broad a repair for this call site.
    /// Healing that directory is <c>UninstallStateStore</c>'s job on the install path,
    /// where the decision belongs. Here an untrusted parent is simply the refusal case —
    /// reported, then declined.
    /// </para>
    /// <para>
    /// <paramref name="commonAppData"/> is a parameter rather than a direct read of
    /// <see cref="Environment.SpecialFolder.CommonApplicationData"/> so the degrade path
    /// is testable without an elevated process and without touching the real
    /// machine-scope state root.
    /// </para>
    /// </remarks>
    [SupportedOSPlatform("windows")]
    internal static string? TryResolveAdminOnlyRoot(string commonAppData, Action<string, bool> report)
    {
        var sigil = Path.Combine(commonAppData, SigilFolder);
        var staging = Path.Combine(sigil, StagingFolder);

#pragma warning disable CA1031 // Fail soft to a user-writable root — but never silently: the cause is reported before returning, and OpenVerified stays the load-bearing half.
        try
        {
            // Create the state root only if it is absent. Never repair an existing one
            // (see the remarks) — CreateHardened would otherwise re-permission and take
            // ownership of S1's directory from the staging path.
            if (!System.IO.Directory.Exists(sigil))
            {
                StateDirectorySecurity.CreateHardened(sigil);
            }

            // The frozen cross-lane predicate (S1) is the ONLY "who can write here?"
            // answer used anywhere in the engine — deliberately not re-implemented.
            if (!StateDirectorySecurity.IsAdminOnlyWritable(sigil))
            {
                Refuse(
                    report,
                    $"the state root '{sigil}' is not administrator-only writable, and it is deliberately " +
                    "not repaired from the staging path — that directory belongs to the install state store");
                return null;
            }

            // The staging directory IS this component's own; creating it hardened, and
            // repairing it if a previous run or an attacker left it permissive, is in
            // scope here in a way that touching the parent is not.
            StateDirectorySecurity.CreateHardened(staging);
            if (!StateDirectorySecurity.IsAdminOnlyWritable(staging))
            {
                Refuse(report, $"the staging root '{staging}' could not be made administrator-only writable");
                return null;
            }

            return staging;
        }
        catch (Exception ex)
        {
            // The exception is NOT swallowed: its type and message are what tell an
            // operator whether this was a redirected %ProgramData%, a denied ACL write,
            // or something an attacker provoked.
            Refuse(report, $"'{staging}' could not be established — {ex.GetType().Name}: {ex.Message}");
            return null;
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Announce that an elevated run failed to obtain an administrator-only staging root
    /// and is therefore refusing to stage at all. Reported as an error, and the same text
    /// is re-thrown by <see cref="ResolveRoot(string?, Action{string, bool}, bool, string)"/>
    /// so the cause reaches a caller with no sink attached.
    /// </summary>
    private static void Refuse(Action<string, bool> report, string reason) =>
        report(
            "staging: REFUSED — this elevated process could not create an " +
            "administrator-only staging directory, and staging a downloaded executable " +
            "in a location the current user can also write would let an unprivileged " +
            $"process substitute what this process launches ({reason})",
            true);

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
/// An elevated run could not obtain an administrator-only staging directory, so nothing
/// was staged. The message carries the underlying cause, because the sink that would
/// otherwise have reported it may not be attached at every call site.
/// </summary>
internal sealed class StagingSecurityException : Exception
{
    public StagingSecurityException(string message)
        : base(message)
    {
    }

    public StagingSecurityException()
    {
    }

    public StagingSecurityException(string message, Exception innerException)
        : base(message, innerException)
    {
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
