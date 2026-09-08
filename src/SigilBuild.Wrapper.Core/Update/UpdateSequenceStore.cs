using System;
using System.Globalization;
using System.IO;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;

namespace SigilBuild.Wrapper.Update;

/// <summary>
/// The highest channel-manifest <c>sequence</c> this machine has ever accepted for a
/// given app + scope (register row R13). Persisted so that a correctly signed but
/// superseded manifest — the replay case a validity window alone cannot catch, because
/// the replayed document may still be inside its own window — is refused.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a separate file rather than a field in <c>uninstall.json</c>.</b> The update
/// sequence is meaningful for an app whose install state has been rewritten, repaired,
/// or replaced, and it must survive an uninstall/reinstall cycle: forgetting the high
/// water mark is precisely the reset an attacker wants. Coupling it to the install
/// journal would tie its lifetime to the wrong thing.
/// </para>
/// <para>
/// <b>Hardening.</b> Machine scope lands under <c>%ProgramData%</c>, whose inherited
/// DACL grants <c>BUILTIN\Users</c> write. This file is a security decision input, so
/// it goes through <see cref="StateDirectorySecurity.CreateHardened"/> exactly as S1
/// made <c>UninstallStateStore</c> do — an unprivileged user who can lower the stored
/// sequence can re-enable every replay this row exists to stop. User scope legitimately
/// lives in the user's own profile, where hardening would be meaningless, and where the
/// user could steer their own update eligibility anyway (the same posture R37 records
/// for <c>minFromVersion</c>: publisher policy, not a security boundary).
/// </para>
/// <para>
/// <b>Fail-safe, not fail-open.</b> An unreadable or corrupt file reads as "no sequence
/// seen" rather than throwing, because a machine that cannot read its own high water
/// mark must still be able to take a genuine update. The write is best-effort for the
/// same reason. This is a deliberate, bounded weakening: it costs an attacker the
/// ability to *delete* the file, which on a hardened machine-scope directory requires
/// the administrator rights that make the whole question moot.
/// </para>
/// </remarks>
internal interface IUpdateSequenceStore
{
    /// <summary>Highest sequence previously accepted, or <c>null</c> on first contact.</summary>
    long? Read(string appId, InstallScope scope);

    /// <summary>Record a new high water mark, if it is higher than what is stored.</summary>
    void Record(string appId, InstallScope scope, long sequence, Action<string, bool>? report);
}

/// <summary>
/// The production <see cref="IUpdateSequenceStore"/>, backed by a file under the
/// per-app state directory.
/// </summary>
/// <remarks>
/// <b>Why this is a seam at all.</b> Every unit test that drives <c>UpdateRunner</c>
/// would otherwise read and WRITE a real <c>%ProgramData%</c> path — and CI runs
/// elevated, so those writes would be live changes on the runner rather than test
/// artifacts. The seam makes the safe thing explicit at each call site instead of
/// depending on every future test remembering to redirect a static.
/// </remarks>
internal sealed class FileUpdateSequenceStore : IUpdateSequenceStore
{
    public static readonly FileUpdateSequenceStore Instance = new();

    public long? Read(string appId, InstallScope scope) => UpdateSequenceStore.Read(appId, scope);

    public void Record(string appId, InstallScope scope, long sequence, Action<string, bool>? report) =>
        UpdateSequenceStore.Record(appId, scope, sequence, report);
}

internal static class UpdateSequenceStore
{
    /// <summary>File name under the per-app state directory.</summary>
    internal const string FileName = "update-sequence.txt";

    /// <summary>
    /// Test seam: redirects the store to a caller-supplied directory so the sequence
    /// logic is exercised without writing to a real <c>%ProgramData%</c>. Machine-scope
    /// tests would otherwise need an elevated runner — and CI runs elevated, so a test
    /// that wrote there would be a live change on the runner.
    /// </summary>
    private static readonly System.Threading.AsyncLocal<string?> DirectoryOverride = new();

    internal static IDisposable UseDirectoryForTesting(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        var previous = DirectoryOverride.Value;
        DirectoryOverride.Value = directory;
        return new RestoreDirectory(previous);
    }

    private sealed class RestoreDirectory : IDisposable
    {
        private readonly string? _previous;

        public RestoreDirectory(string? previous) => _previous = previous;

        public void Dispose() => DirectoryOverride.Value = _previous;
    }

    internal static string PathFor(string appId, InstallScope scope) =>
        Path.Combine(
            DirectoryOverride.Value ?? UninstallStateStore.DirectoryFor(appId, scope),
            FileName);

    /// <summary>
    /// The highest sequence previously accepted for this app + scope, or <c>null</c>
    /// when none has been recorded (first update check on this machine).
    /// </summary>
    internal static long? Read(string appId, InstallScope scope)
    {
        ArgumentException.ThrowIfNullOrEmpty(appId);
        try
        {
            var path = PathFor(appId, scope);
            if (!File.Exists(path))
            {
                return null;
            }
            var text = File.ReadAllText(path).Trim();
            return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                && value >= 0
                    ? value
                    : null;
        }
#pragma warning disable CA1031 // An unreadable high water mark must not break a genuine update; see the fail-safe note on the type.
        catch (Exception)
        {
            return null;
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Record <paramref name="sequence"/> as the new high water mark, if it is higher
    /// than what is already stored. Best-effort: a failure to persist is reported, never
    /// thrown, because the update it accompanies has already been authenticated and
    /// judged fresh.
    /// </summary>
    internal static void Record(string appId, InstallScope scope, long sequence, Action<string, bool>? report = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(appId);
        try
        {
            var existing = Read(appId, scope);
            if (existing is not null && existing >= sequence)
            {
                return;
            }

            var path = PathFor(appId, scope);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                if (DirectoryOverride.Value is null && scope == InstallScope.Machine && OperatingSystem.IsWindows())
                {
                    StateDirectorySecurity.CreateHardened(dir);
                }
                else
                {
                    Directory.CreateDirectory(dir);
                }
            }

            File.WriteAllText(path, sequence.ToString(CultureInfo.InvariantCulture));
        }
#pragma warning disable CA1031 // Best-effort persistence; the caller has already decided the update is authentic and fresh.
        catch (Exception ex)
        {
            report?.Invoke($"update: could not record the update sequence high water mark — {ex.Message}", true);
        }
#pragma warning restore CA1031
    }
}
