using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;

namespace SigilBuild.Wrapper.Update;

/// <summary>
/// The inputs the <c>/Update</c> runtime needs, resolved from the embedded blob +
/// the resolved scope. Kept a plain record so <see cref="UpdateRunner"/> is driven
/// by data, not by an <see cref="InstallSession"/>, and is unit-testable in isolation.
/// </summary>
internal sealed record UpdateRequest(
    string? ManifestUrl,
    string? SigningKey,
    string? Channel,
    InstallScope Scope,
    string AppId,
    string TempDirectory,
    // T12.4: whether the downloaded child Setup.exe is launched silently.
    // Headless /Update (T12.3, unchanged) launches it /silent, forwarding only
    // the scope flag's twin; a headed, non-silent /Update launches it WITHOUT
    // /silent so the user sees the new version's own install wizard. Defaults to
    // true so every T12.3-era call site (and test) keeps its original behavior
    // without having to name this argument.
    bool SilentChild = true);

/// <summary>
/// The core of P12 (T12.3): the headless <c>/Update</c> flow — fetch the signed
/// channel manifest + its detached signature, parse (SIG0320) and verify (SIG0321,
/// a HARD reject), compare the advertised version against the installed one reusing
/// the P3 upgrade decision, and — only when a strictly-newer package is available —
/// download the new version's stamped Setup.exe and run it SILENTLY, forwarding the
/// current scope, so that child installer performs the actual version-aware P3
/// upgrade (uninstall-old-then-install, preserving the install dir). This process
/// re-implements no install logic; it propagates the child's exit code.
/// </summary>
/// <remarks>
/// The three I/O boundaries (HTTP fetch, package download, child launch) are behind
/// the <see cref="IUpdateResourceFetcher"/> / <see cref="IUpdatePackageDownloader"/>
/// / <see cref="IChildInstallerLauncher"/> seams, and the installed version is read
/// through an injected probe, so the decision table is exercised by unit tests with
/// plain doubles. The live fetch → download → run-child leg is CI-VM-only (T12.6).
/// </remarks>
internal sealed class UpdateRunner
{
    private readonly IUpdateResourceFetcher _fetcher;
    private readonly IUpdatePackageDownloader _downloader;
    private readonly IChildInstallerLauncher _launcher;
    private readonly Func<UpgradeState> _installedStateProbe;
    private readonly Action<string, bool> _report;
    private readonly IUpdateSequenceStore _sequences;

    /// <param name="sequences">
    /// R13's replay high water mark. Defaults to the real machine-scope file store;
    /// tests MUST pass an in-memory one, because the default reads and writes a real
    /// <c>%ProgramData%</c> path and CI runs elevated.
    /// </param>
    public UpdateRunner(
        IUpdateResourceFetcher fetcher,
        IUpdatePackageDownloader downloader,
        IChildInstallerLauncher launcher,
        Func<UpgradeState> installedStateProbe,
        Action<string, bool> report,
        IUpdateSequenceStore? sequences = null)
    {
        _fetcher = fetcher;
        _downloader = downloader;
        _launcher = launcher;
        _installedStateProbe = installedStateProbe;
        _report = report;
        _sequences = sequences ?? FileUpdateSequenceStore.Instance;
    }

    public async Task<int> RunAsync(UpdateRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 1. Not update-enabled: the manifest declared no `updates:` block, so there
        //    is nothing to check. Distinct from a malformed invocation (exit 64).
        if (string.IsNullOrWhiteSpace(request.ManifestUrl))
        {
            _report("update: this installer is not update-enabled (no updates.manifestUrl configured)", true);
            return InstallSession.UpdateNotConfiguredExitCode;
        }

        // R14: re-check the scheme before anything is fetched. SIG0324 catches this at
        // pack time; this is the runtime half, and it is not redundant — the `.sig` URL
        // is this string + ".sig", so a cleartext manifestUrl silently drags the
        // signature fetch onto cleartext too, and an installer stamped before SIG0324
        // existed is still out there.
        if (!request.ManifestUrl!.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            _report(
                $"update: refusing to check for updates — updates.manifestUrl must be https:// " +
                $"(got '{request.ManifestUrl}')",
                true);
            return InstallSession.UpdateCheckFailedExitCode;
        }

        var channelLabel = string.IsNullOrWhiteSpace(request.Channel) ? "default" : request.Channel!;
        _report($"update: checking for updates on channel '{channelLabel}' at {request.ManifestUrl}", false);

        // 2. Fetch the channel manifest bytes + the detached signature. Parse and
        //    verify the SAME bytes (verification is over the exact fetched bytes).
        var manifestFetch = await _fetcher.FetchAsync(request.ManifestUrl!, ct).ConfigureAwait(false);
        if (!manifestFetch.Success || manifestFetch.Bytes is null)
        {
            _report($"update: could not check for updates — {manifestFetch.Error}", true);
            return InstallSession.UpdateCheckFailedExitCode;
        }

        var signatureUrl = request.ManifestUrl! + ".sig";
        var signatureFetch = await _fetcher.FetchAsync(signatureUrl, ct).ConfigureAwait(false);
        if (!signatureFetch.Success || signatureFetch.Bytes is null)
        {
            _report($"update: could not fetch the channel manifest signature — {signatureFetch.Error}", true);
            return InstallSession.UpdateCheckFailedExitCode;
        }

        var manifestBytes = manifestFetch.Bytes;

        // 3. Verify the detached signature over the exact bytes FIRST (SIG0321). A
        //    tampered or unsigned channel manifest is a HARD reject — never acted on,
        //    and now never even parsed. R39: verification used to run after the parse.
        //    Nothing parsed was consumed before verification, so that ordering was not
        //    exploitable — but it exposed the JSON parser to unverified network input
        //    and let whoever answered the request choose which diagnostic the user saw.
        //    Verify-then-parse is the cheaper invariant to keep true as this grows.
        var signatureBase64 = Encoding.UTF8.GetString(signatureFetch.Bytes).Trim();
        var verify = ChannelManifestVerifier.Verify(manifestBytes, signatureBase64, request.SigningKey);
        if (!verify.Success)
        {
            _report($"update: {verify.DiagnosticCode}: {verify.Error}", true);
            return InstallSession.UpdateManifestRejectedExitCode;
        }

        // 4. Parse the now-authenticated bytes (SIG0320 on failure).
        var parse = ChannelManifestParser.Parse(Encoding.UTF8.GetString(manifestBytes));
        if (!parse.Success || parse.Manifest is null)
        {
            _report($"update: {parse.DiagnosticCode}: {parse.Error}", true);
            return InstallSession.UpdateCheckFailedExitCode;
        }
        var channel = parse.Manifest;

        // 4b. R13: freshness. The signature proves WHO minted this document, not WHEN.
        //     Without this gate an on-path attacker or compromised CDN replays a
        //     correctly signed older manifest indefinitely — freezing updates while a
        //     security fix exists, or steering the client onto an intermediate version
        //     that is newer than installed and known-vulnerable.
        var lastSequence = _sequences.Read(request.AppId, request.Scope);
        var freshness = EvaluateFreshness(channel, lastSequence, DateTimeOffset.UtcNow);
        if (freshness is not null)
        {
            _report($"update: {freshness}", true);
            return InstallSession.UpdateManifestRejectedExitCode;
        }

        // Advance the high water mark as soon as the manifest is authenticated and
        // judged fresh — not after the install succeeds. The sequence records the newest
        // manifest this machine has SEEN and believed, which is what a later replay has
        // to beat; whether the package it advertised installed cleanly is a different
        // question, and tying the two would let a failed install reopen the replay window.
        if (channel.Sequence is { } seen)
        {
            _sequences.Record(request.AppId, request.Scope, seen, _report);
        }

        // 5. Compare the advertised version against the installed one, reusing the P3
        //    upgrade decision (dotted-version comparison; malformed installed treated
        //    as older). Same/older → up to date (clean exit 0).
        var state = _installedStateProbe();
        var installedLabel = state.Found && !string.IsNullOrEmpty(state.InstalledVersion)
            ? state.InstalledVersion
            : "(none)";
        var plan = UpgradePlanner.Decide(state, channel.Version, forceDowngrade: false);
        var newerAvailable = plan.Action is UpgradeAction.Upgrade or UpgradeAction.Fresh;
        if (!newerAvailable)
        {
            _report($"update: up to date (installed {installedLabel}, channel {channel.Version})", false);
            return 0;
        }

        // Honor the channel manifest's MinFromVersion floor: an installed version below
        // it cannot take this package via the setup-runtime path (e.g. a full package
        // with a delta-from floor). Only blocks on a KNOWN below-floor installed version.
        if (!string.IsNullOrWhiteSpace(channel.MinFromVersion) && state.Found)
        {
            if (VersionComparison.IsWellFormed(state.InstalledVersion))
            {
                if (VersionComparison.Compare(state.InstalledVersion, channel.MinFromVersion) < 0)
                {
                    _report(
                        $"update: cannot update to {channel.Version} — installed {installedLabel} is below the minimum " +
                        $"{channel.MinFromVersion} this package updates from",
                        true);
                    return InstallSession.UpdateNotEligibleExitCode;
                }
            }
            else
            {
                // R37: an installed version that cannot be compared against the floor is
                // NOT eligible. This used to log and proceed, which let anything that
                // could make the recorded version unparseable — for a user-scope install
                // that is a value in the user's own HKCU — skip a floor the publisher
                // declared. Fail closed: a floor that is unenforceable is not a floor
                // that has been satisfied.
                _report(
                    $"update: cannot update to {channel.Version} — this package declares a minimum " +
                    $"{channel.MinFromVersion} it updates from, and the installed version " +
                    $"'{installedLabel}' is malformed and cannot be compared against it",
                    true);
                return InstallSession.UpdateNotEligibleExitCode;
            }
        }

        // 6. Validate the checksum is a plausible SHA-256 hex digest before spending a
        //    download on it (T12.1 left Sha256 permissive). A bad-format checksum fails
        //    cleanly here rather than surfacing later as a confusing "sha256 mismatch".
        if (!IsPlausibleSha256Hex(channel.Sha256))
        {
            _report("update: channel manifest sha256 is not a 64-character hex digest — refusing to download", true);
            return InstallSession.UpdateCheckFailedExitCode;
        }

        _report($"update: newer version available (installed {installedLabel} → {channel.Version})", false);

        // 7. Download the new version's stamped Setup.exe into a private, per-run
        //    staging directory and run it in the current scope. The staged file is
        //    re-verified from an OPEN, write-and-delete-denying handle that is held
        //    across the child launch — register row R12: verifying and then launching a
        //    file nobody is holding leaves a window in which the bytes can be swapped.
        //    Disposing the staging directory cleans up regardless of outcome.
        var stagedName = $"sigil-update-{SanitizeSegment(request.AppId)}.exe";

        SecureStaging created;
        try
        {
            // _report also carries SecureStaging's own refusal line: an ELEVATED run that
            // cannot obtain an administrator-only staging directory throws rather than
            // degrading to a user-writable one, and the cause arrives here first. That must
            // never be swallowed; the catch below turns it into a typed exit code.
            created = SecureStaging.Create("update", _report, request.TempDirectory);
        }
#pragma warning disable CA1031 // A staging failure becomes the same typed exit code as every other failure here; a redirected or ACL-hostile temp directory must not crash the host, which has no general catch.
        catch (Exception ex)
        {
            _report($"update: could not create a private staging directory for the download — {ex.Message}", true);
            return InstallSession.UpdateCheckFailedExitCode;
        }
#pragma warning restore CA1031

        using var staging = created;
        var dest = staging.PathFor(stagedName);

        var download = await _downloader
            .DownloadAsync(channel.PackageUrl, dest, channel.Sha256, ct)
            .ConfigureAwait(false);
        if (!download.Success)
        {
            _report($"update: download failed — {download.Error}", true);
            return InstallSession.UpdateCheckFailedExitCode;
        }

        FileStream handle;
        try
        {
            handle = staging.OpenVerified(stagedName, channel.Sha256);
        }
        catch (Exception ex) when (
            ex is StagedFileVerificationException or IOException or UnauthorizedAccessException)
        {
            // Fail closed. A downloaded package that no longer matches the sha256 it was
            // verified under is not a transient problem — it is the attack.
            _report($"update: refusing to run the downloaded installer — {ex.Message}", true);
            return InstallSession.UpdateCheckFailedExitCode;
        }

        using (handle)
        {
            // R11: Authenticode, immediately before the launch and from inside the window
            // where the verified handle is already held. The channel manifest's signature
            // authenticates the sha256, and the sha256 authenticates the bytes — but only
            // against the manifest, and this process is about to run those bytes with the
            // current scope's privileges. Armed only when THIS artifact declared signing:
            // an installer that never claimed a signed provenance has no standing to
            // demand one of its own successor, and would simply lose /Update entirely.
            // When it is NOT armed, RefusalForArtifactDownload says so on this same report
            // channel rather than passing quietly — an absent check nobody is told about is
            // indistinguishable, in a log, from a check that passed.
            var trustRefusal = DownloadedBinaryTrust.RefusalForArtifactDownload(
                dest, $"the downloaded {channel.Version} installer", _report);
            if (trustRefusal is not null)
            {
                _report($"update: {trustRefusal}", true);
                return InstallSession.UpdateManifestRejectedExitCode;
            }

            var scopeFlag = request.Scope == InstallScope.Machine ? "/allusers" : "/currentuser";
            // T12.4: headless /Update (SilentChild true, T12.3 unchanged) launches the
            // child /silent; a headed, non-silent /Update launches it WITHOUT /silent so
            // the user sees the new version's own install wizard.
            var args = request.SilentChild ? new[] { scopeFlag, "/silent" } : new[] { scopeFlag };
            var argsDescription = request.SilentChild ? $"{scopeFlag} /silent" : scopeFlag;
            _report($"update: installing {channel.Version} (running the downloaded setup {argsDescription})", false);

            int childCode;
            try
            {
                childCode = await _launcher.RunAsync(dest, args, ct).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Surface a spawn/wait failure as a typed exit code; nothing was installed by this process.
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _report($"update: could not run the downloaded installer — {ex.Message}", true);
                return InstallSession.UpdateCheckFailedExitCode;
            }
#pragma warning restore CA1031

            _report($"update: setup exited with code {childCode}", childCode != 0 && childCode != InstallSession.RebootRequiredExitCode);
            return childCode;
        }
    }

    /// <summary>
    /// Clock-skew tolerance applied to both ends of the validity window (register row
    /// R13, ADR-011).
    /// </summary>
    /// <remarks>
    /// A window with no skew allowance breaks on any misconfigured clock, and a machine
    /// whose clock is wrong is overwhelmingly more common than one under an on-path
    /// replay. Five minutes is the usual Kerberos-era allowance and is far below the
    /// timescale of the attack this bounds (a freeze attack is interesting over days,
    /// not minutes), so granting it costs the defence nothing measurable.
    /// </remarks>
    internal static readonly TimeSpan ClockSkewTolerance = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Maximum age accepted from <c>issuedAt</c>, independently of <c>expiresAt</c>
    /// (register row R13, ADR-011).
    /// </summary>
    /// <remarks>
    /// <c>expiresAt</c> is publisher-chosen, so a publisher who sets it to the year 3000
    /// — by mistake or on the advice of a "make the warnings stop" search result — opts
    /// out of the entire defence without knowing it. This is the ceiling the client
    /// enforces regardless: whatever the document says, a manifest older than this is not
    /// acted on.
    /// </remarks>
    internal static readonly TimeSpan MaxManifestAge = TimeSpan.FromDays(30);

    /// <summary>
    /// The R13 freshness decision, pure and unit-testable: returns the refusal reason, or
    /// <c>null</c> when <paramref name="channel"/> is fresh enough to act on.
    /// </summary>
    /// <param name="channel">The verified, parsed channel manifest.</param>
    /// <param name="lastSequence">
    /// The highest sequence this machine has previously accepted, or <c>null</c> on first
    /// contact.
    /// </param>
    /// <param name="now">Current UTC time, injected so the window is testable.</param>
    internal static string? EvaluateFreshness(ChannelManifest channel, long? lastSequence, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(channel);

        // The parser has already established that all three fields are present and
        // well-formed, so a failure to re-parse here would be a contract violation
        // rather than hostile input — but this method is also called directly by tests,
        // so it re-derives rather than assuming.
        if (!ChannelManifestParser.TryParseTimestamp(channel.IssuedAt, out var issuedAt))
        {
            return $"channel manifest has no usable 'issuedAt' (got '{channel.IssuedAt}') — refusing to act on it";
        }

        if (!ChannelManifestParser.TryParseTimestamp(channel.ExpiresAt, out var expiresAt))
        {
            return $"channel manifest has no usable 'expiresAt' (got '{channel.ExpiresAt}') — refusing to act on it";
        }

        if (now > expiresAt + ClockSkewTolerance)
        {
            return
                $"channel manifest is stale — it expired at {expiresAt:O} and it is now {now:O}. " +
                "Refusing to act on a correctly signed but expired manifest (a replayed manifest is " +
                "signed just as validly as a current one).";
        }

        if (now > issuedAt + MaxManifestAge + ClockSkewTolerance)
        {
            return
                $"channel manifest is stale — it was issued at {issuedAt:O}, more than " +
                $"{MaxManifestAge.TotalDays:0} days ago, which exceeds the maximum age this client " +
                "accepts regardless of the expiry the manifest declares.";
        }

        if (issuedAt > now + ClockSkewTolerance)
        {
            return
                $"channel manifest is not yet valid — it declares issuedAt {issuedAt:O}, which is in " +
                $"the future relative to {now:O}. Refusing rather than guessing which clock is wrong.";
        }

        if (expiresAt < issuedAt)
        {
            return
                $"channel manifest declares expiresAt {expiresAt:O} before issuedAt {issuedAt:O} — " +
                "an empty validity window is malformed, not permissive.";
        }

        if (channel.Sequence is not { } sequence)
        {
            return "channel manifest has no 'sequence' — refusing to act on it";
        }

        if (lastSequence is { } previous && sequence < previous)
        {
            return
                $"channel manifest sequence {sequence} is lower than {previous}, which this machine has " +
                "already accepted — refusing a rollback to a superseded manifest.";
        }

        return null;
    }

    /// <summary>
    /// True when <paramref name="value"/> is exactly 64 hexadecimal characters — the
    /// shape of a SHA-256 digest the P4 downloader compares against (it hex-encodes
    /// the computed hash). Anything else (base64, wrong length, non-hex) is rejected
    /// up front so a malformed checksum fails cleanly instead of always mismatching.
    /// </summary>
    internal static bool IsPlausibleSha256Hex(string? value)
    {
        if (value is null)
        {
            return false;
        }
        var s = value.Trim();
        if (s.Length != 64)
        {
            return false;
        }
        foreach (var c in s)
        {
            var isHex = c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            if (!isHex)
            {
                return false;
            }
        }
        return true;
    }

    private static string SanitizeSegment(string appId)
    {
        var sb = new StringBuilder(appId.Length);
        foreach (var c in appId)
        {
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_');
        }
        var s = sb.ToString();
        return s.Length == 0 ? "app" : s;
    }
}
