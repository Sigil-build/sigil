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
/// <b>Where it stages.</b> When the process is elevated the directory is
/// <c>%ProgramData%\sigil-{purpose}-{guid}</c> — a <em>direct child</em> of
/// <c>%ProgramData%</c>, created hardened and then <em>confirmed</em> with
/// <see cref="StateDirectorySecurity.IsAdminOnlyWritable"/>, the one shared "who can
/// write here?" predicate (this type deliberately implements no second ACL check).
/// Unelevated there is no admin-only location to reach, so it falls back to the caller's
/// root (or <c>%TEMP%</c>) and still applies a <em>protected</em>
/// (inheritance-discarding) DACL granting only the current user, SYSTEM and
/// <c>BUILTIN\Administrators</c>. That is weaker by construction — an unelevated process
/// cannot create something it cannot itself modify — which is why
/// <see cref="OpenVerified"/>, not the directory ACL, is the load-bearing half.
/// </para>
/// <para>
/// <b>Why a direct child of <c>%ProgramData%</c>, and nothing named <c>Sigil</c>.</b>
/// <c>%ProgramData%\Sigil</c> is the install-state store's root; this type must not
/// repair it, and any unprivileged user can create it (register row R1's attack). Siting
/// staging beneath it therefore made the refusal below into a denial of service anyone
/// could trigger. A per-run GUID directly under <c>%ProgramData%</c> has no fixed name to
/// squat, and <c>%ProgramData%</c> grants <c>BUILTIN\Users</c>
/// <c>(CI)(WD,AD,WEA,WA)</c> — create-child — but not <c>DC</c>, so a non-administrator
/// can add siblings and can neither delete nor replace one it does not own. Nothing an
/// unprivileged user can do makes this refuse.
/// </para>
/// <para>
/// <b>An elevated run that cannot get an administrator-only directory refuses.</b> It
/// does not stage in a user-writable one and carry on. The reason is announced on the
/// <c>report</c> callback first — exception type and message included — and the same
/// cause is carried by the thrown <see cref="StagingSecurityException"/>, so it survives
/// even where no sink is attached.
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
/// destination name before creating it. With the siting above, that refusal now only
/// fires on a genuinely broken environment, not on anything an attacker can arrange.
/// </para>
/// <para>
/// <b>Unelevated is not a degrade.</b> There is no admin-only location an unelevated
/// process can reach, so <c>%TEMP%</c> is the only option there is, not a downgrade; it
/// is taken silently and <see cref="OpenVerified"/> carries the guarantee.
/// </para>
/// </remarks>
internal sealed class SecureStaging : IDisposable
{
    /// <summary>
    /// Test seam: forces the siting <see cref="Create(string, Action{string, bool}, string?)"/>
    /// would otherwise read from the environment. <see cref="AsyncLocal{T}"/> rather than a
    /// plain static so the override is scoped to the test that set it and flows into the
    /// engine's async work — xUnit runs collections in parallel, and a global would leak
    /// across them.
    /// </summary>
    /// <remarks>
    /// This exists because the alternative is unacceptable: without it, any test that
    /// resolves <c>{staging_dir}</c> writes into the real <c>%ProgramData%</c> whenever the
    /// process happens to be elevated — which CI is. Same shape and same rationale as
    /// <c>SigilHttpClient.UseForTesting</c>.
    /// </remarks>
    private static readonly AsyncLocal<Siting?> SitingOverride = new();

    /// <summary>
    /// Process-wide test floor: when set, the machine-wide (elevated) siting is never
    /// taken, whatever the process's real token says. Set once per test assembly.
    /// </summary>
    /// <remarks>
    /// A per-test opt-in is not enough on its own. Staging is reached transitively — the
    /// prerequisite runner, the update runner and any <c>{staging_dir}</c> resolution all
    /// funnel here — so a test that never mentions <c>SecureStaging</c> can still put a
    /// directory in the real <c>%ProgramData%</c> and launch a binary out of it the moment
    /// the runner happens to be elevated, which on CI it always is. Making the safe
    /// answer the assembly-wide default means a future test cannot reintroduce that by
    /// omission. Only the elevated <em>branch</em> is disabled; a caller's own fallback
    /// root is untouched, so tests that assert where they staged still see their own root.
    /// </remarks>
    private static bool _neverElevatedForTesting;

    private bool _disposed;

    /// <summary>
    /// The environment facts that decide where a staging directory goes. <c>FallbackRoot</c>
    /// is test-only and, when set, replaces the caller's own fallback argument so a test can
    /// keep every byte it stages inside a scratch directory.
    /// </summary>
    internal readonly record struct Siting(bool Elevated, string CommonAppData, string? FallbackRoot);

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
    /// Create a private staging directory named <c>sigil-{purpose}-{guid}</c> — directly
    /// under <c>%ProgramData%</c> when elevated, otherwise under
    /// <paramref name="fallbackRoot"/> (default <c>%TEMP%</c>) — with a protected,
    /// non-inherited DACL, and confirm an elevated one really is administrator-only
    /// before handing it back.
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
    /// not pass one loses the refusal line with no compile error to catch it. Making it
    /// required means the omission cannot be made by accident; passing a sink that
    /// discards has to be a decision someone writes down.
    /// </para>
    /// <para>
    /// It says "refusal", not "degrade": this type stopped degrading when register row
    /// R5's residual was closed. There is no downgraded elevated run left to announce —
    /// there is a refusal, and that is the line that must reach a human.
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
        if (SitingOverride.Value is { } siting)
        {
            return Create(
                purpose, report, siting.FallbackRoot ?? fallbackRoot, siting.Elevated, siting.CommonAppData);
        }

        return Create(
            purpose,
            report,
            fallbackRoot,
            elevated: !_neverElevatedForTesting
                      && OperatingSystem.IsWindows()
                      && Elevation.IsProcessElevated(),
            commonAppData: Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
    }

    /// <summary>
    /// Test seam (internal): for the rest of this process, never take the machine-wide
    /// (elevated) siting — so nothing a test does, directly or transitively, can create a
    /// directory in the real <c>%ProgramData%</c> or launch a binary out of one. Called
    /// once from each test assembly's bootstrap. Not for production use.
    /// </summary>
    internal static void NeverStageElevatedForTesting() => _neverElevatedForTesting = true;

    /// <summary>
    /// Test seam (internal): make every <see cref="Create(string, Action{string, bool}, string?)"/>
    /// on this async flow stage inside <paramref name="scratchRoot"/> instead of reading the
    /// real environment, until the returned scope is disposed. Not for production use.
    /// </summary>
    /// <remarks>
    /// This is a hard requirement, not a convenience. Without it a test that resolves
    /// <c>{staging_dir}</c> through the production path writes into the real
    /// <c>%ProgramData%</c> on any elevated host — and CI runs elevated — so it would
    /// create directories there, execute binaries out of them, and leave them behind.
    /// Every test that reaches staging wraps itself in this, including the ones that go
    /// through <c>InstallSession</c>, which builds its own context and offers no seam.
    /// <paramref name="elevated"/> defaults to <c>false</c> so a test cannot ask for the
    /// administrator-only siting by accident.
    /// </remarks>
    internal static IDisposable UseSitingForTesting(string scratchRoot, bool elevated = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(scratchRoot);
        var previous = SitingOverride.Value;
        SitingOverride.Value = new Siting(elevated, scratchRoot, scratchRoot);
        return new RestoreSiting(previous);
    }

    private sealed class RestoreSiting : IDisposable
    {
        private readonly Siting? _previous;

        public RestoreSiting(Siting? previous) => _previous = previous;

        public void Dispose() => SitingOverride.Value = _previous;
    }

    /// <summary>
    /// The creation itself, with the two environment facts it depends on passed in so
    /// the elevated branch is reachable from an unelevated test process — the only way
    /// to assert the refusal on a developer box or an unelevated runner.
    /// </summary>
    internal static SecureStaging Create(
        string purpose,
        Action<string, bool> report,
        string? fallbackRoot,
        bool elevated,
        string commonAppData)
    {
        ArgumentException.ThrowIfNullOrEmpty(purpose);
        ArgumentNullException.ThrowIfNull(report);

        var (root, wantAdminOnly) = ResolveRoot(fallbackRoot, report, elevated, commonAppData);

        // The GUID is the whole anti-squatting story on the elevated path: %ProgramData%
        // lets any user CREATE a child, so a fixed name could be pre-created by a
        // non-administrator. An unguessable one cannot be, and once it exists hardened
        // it cannot be deleted or replaced either — %ProgramData% grants BUILTIN\Users
        // (CI)(WD,AD,WEA,WA) but not DC.
        var directory = Path.Combine(root, $"sigil-{Sanitize(purpose)}-{Guid.NewGuid():N}");

        if (!OperatingSystem.IsWindows())
        {
            // Off Windows there are no ACLs to apply; the wrapper only ships on
            // Windows, but this assembly is built and unit-tested cross-platform.
            System.IO.Directory.CreateDirectory(directory);
            return new SecureStaging(directory, wantAdminOnly);
        }

        if (!wantAdminOnly)
        {
            CreatePrivateDirectory(directory, adminOnly: false, report);
            return new SecureStaging(directory, false);
        }

        // Elevated from here down. Every way this can fail — the create throwing, or the
        // created directory not confirming — ends in the SAME typed refusal, so no caller
        // has to distinguish "could not" from "did not" and no raw IOException escapes as
        // an unhandled crash.
        try
        {
            CreatePrivateDirectory(directory, adminOnly: true, report);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Refuse(report, $"'{directory}' could not be created — {ex.GetType().Name}: {ex.Message}");
            throw new StagingSecurityException(
                $"'{directory}' could not be created, so this elevated process will not stage a downloaded " +
                $"executable ({ex.GetType().Name}: {ex.Message})",
                ex);
        }

        // Confirmed on the directory that will actually hold the download, with the one
        // frozen predicate — not inferred from the root. An elevated process that somehow
        // produced a directory it does not exclusively own removes it and refuses, rather
        // than staging an executable there.
        if (!StateDirectorySecurity.IsAdminOnlyWritable(directory))
        {
            TryRemove(directory);
            Refuse(report, $"'{directory}' could not be made administrator-only writable");
            throw new StagingSecurityException(
                $"'{directory}' could not be made administrator-only writable, so this elevated process " +
                "will not stage a downloaded executable there");
        }

        return new SecureStaging(directory, true);
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
    /// The directory the per-run staging directory is created <em>in</em>:
    /// <c>%ProgramData%</c> itself when elevated, otherwise the caller's fallback (or
    /// <c>%TEMP%</c>). The bool is whether the per-run directory must then be
    /// administrator-only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Nothing named <c>Sigil</c> is created or touched here.</b> An earlier revision
    /// sited the elevated root at <c>%ProgramData%\Sigil\staging</c> and refused when the
    /// intermediate <c>Sigil</c> directory was not administrator-only — but that
    /// directory is the install-state store's, must not be repaired from the staging
    /// path, and can be created by any unprivileged user (register row R1's attack).
    /// The refusal was therefore a denial of service anyone could trigger. Creating the
    /// per-run directory as a <b>direct child of <c>%ProgramData%</c></b> removes the
    /// lever entirely: there is no fixed name to squat, and
    /// <c>%ProgramData%</c> grants <c>BUILTIN\Users</c> <c>(CI)(WD,AD,WEA,WA)</c> —
    /// create-child — but not <c>DC</c>, so a non-administrator can add siblings and can
    /// neither delete nor replace one it does not own.
    /// </para>
    /// <para>
    /// <c>%ProgramData%</c> is never <em>created</em>: it is an OS directory, and if it
    /// is missing or is not a directory then something is wrong enough that guessing is
    /// worse than refusing.
    /// </para>
    /// </remarks>
    /// <exception cref="StagingSecurityException">
    /// This process is elevated and no administrator-only location could be reached.
    /// See the type remarks for why that is a refusal rather than a fallback.
    /// </exception>
    internal static (string Root, bool AdminOnly) ResolveRoot(
        string? fallbackRoot, Action<string, bool> report, bool elevated, string commonAppData)
    {
        // Only an ELEVATED run can reach an admin-only location, and only an elevated run
        // is giving anything up by not having one: staging in %TEMP% unelevated is the
        // only option there is, not a downgrade, and must not cry wolf.
        if (!elevated)
        {
            var fallback = string.IsNullOrWhiteSpace(fallbackRoot) ? Path.GetTempPath() : fallbackRoot!;
            System.IO.Directory.CreateDirectory(fallback);
            return (fallback, false);
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new StagingSecurityException(
                "an administrator-only staging directory is a Windows-only concept, so this elevated " +
                "run cannot stage a downloaded executable safely");
        }

        if (string.IsNullOrWhiteSpace(commonAppData) || !System.IO.Directory.Exists(commonAppData))
        {
            var reason = $"'{commonAppData}' does not exist as a directory";
            Refuse(report, reason);
            throw new StagingSecurityException(
                "this elevated process could not establish an administrator-only staging directory: " + reason);
        }

        return (commonAppData, true);
    }

    // NOTE — there is deliberately NO orphan sweep here, and adding one back needs more
    // care than it looks. A previous revision swept `sigil-*` siblings older than 24 h.
    // Two independent defects came out of it:
    //
    //   * the glob also matched %ProgramData%\sigil-runtime, the native-runtime DLL cache
    //     — so resolving {staging_dir} could recursively delete the very directory the
    //     same process was loading Skia and ANGLE from, mid-install;
    //   * both guards (creation time, ACL) were read THROUGH the candidate path, so a
    //     junction planted by a user resolved to an admin-owned target and passed them,
    //     and the age guard is attacker-settable anyway.
    //
    // What it bought was hygiene, not a security property: without it, abandoned staging
    // directories accumulate in %ProgramData% after a crash or a kill. That is a
    // housekeeping cost, and a far better trade than a delete loop next to a directory
    // the process is executing from. If it ever comes back it must match the exact
    // per-run shape (its own prefix plus a 32-hex GUID), never a bare `sigil-*`, must
    // refuse to follow reparse points, and must be tested.

    /// <summary>Best-effort removal of a directory this call just created and rejected.</summary>
    private static void TryRemove(string directory)
    {
#pragma warning disable CA1031 // Cleanup of our own failed creation; the refusal is what matters.
        try
        {
            if (System.IO.Directory.Exists(directory))
            {
                System.IO.Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // Leaving it behind is untidy, never unsafe: nothing was staged in it.
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Announce that an elevated run failed to obtain an administrator-only staging
    /// directory and is therefore refusing to stage at all. Reported as an error, and the
    /// same cause is carried by the thrown <see cref="StagingSecurityException"/> so it
    /// reaches a caller with no sink attached.
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
    private static void CreatePrivateDirectory(string directory, bool adminOnly, Action<string, bool> report)
    {
        if (adminOnly)
        {
            // Elevated: SYSTEM + Administrators full control, Users read-execute. The
            // elevated caller is itself a member of BUILTIN\Administrators, so it needs
            // no ACE of its own — adding one would make the directory non-admin-only.
            // Passing the sink, not discarding it: CreateHardened announces here when it
            // TAKES OWNERSHIP of a pre-existing directory, and an ownership change on a
            // machine-scope path is not something an install may perform silently.
            StateDirectorySecurity.CreateHardened(directory, ReportSink.For(report));
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
