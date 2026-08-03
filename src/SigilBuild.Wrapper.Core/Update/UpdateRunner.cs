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

    public UpdateRunner(
        IUpdateResourceFetcher fetcher,
        IUpdatePackageDownloader downloader,
        IChildInstallerLauncher launcher,
        Func<UpgradeState> installedStateProbe,
        Action<string, bool> report)
    {
        _fetcher = fetcher;
        _downloader = downloader;
        _launcher = launcher;
        _installedStateProbe = installedStateProbe;
        _report = report;
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

        // 3. Parse (SIG0320 on failure).
        var parse = ChannelManifestParser.Parse(Encoding.UTF8.GetString(manifestBytes));
        if (!parse.Success || parse.Manifest is null)
        {
            _report($"update: {parse.DiagnosticCode}: {parse.Error}", true);
            return InstallSession.UpdateCheckFailedExitCode;
        }
        var channel = parse.Manifest;

        // 4. Verify the detached signature over the exact bytes (SIG0321). A tampered
        //    or unsigned channel manifest is a HARD reject — never acted on.
        var signatureBase64 = Encoding.UTF8.GetString(signatureFetch.Bytes).Trim();
        var verify = ChannelManifestVerifier.Verify(manifestBytes, signatureBase64, request.SigningKey);
        if (!verify.Success)
        {
            _report($"update: {verify.DiagnosticCode}: {verify.Error}", true);
            return InstallSession.UpdateManifestRejectedExitCode;
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
                // T12.3 Minor: the installed version is malformed (can't be compared
                // against MinFromVersion), so the floor is skipped rather than blocking
                // — but that must be an OBSERVABLE decision, not a silent no-op.
                _report(
                    $"update: minFromVersion floor {channel.MinFromVersion} skipped — installed version " +
                    $"'{installedLabel}' is malformed and cannot be compared",
                    false);
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
        using var staging = SecureStaging.Create("update", request.TempDirectory);
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
