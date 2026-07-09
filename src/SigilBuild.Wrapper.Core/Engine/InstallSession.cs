using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Cli;

namespace SigilBuild.Wrapper.Engine;

/// <summary>
/// Outcome of a single install run: success, or failure with the engine's
/// error message (the rollback has already completed by the time this is
/// returned). Cancellation is surfaced as a thrown
/// <see cref="OperationCanceledException"/>, not an <see cref="InstallOutcome"/>.
/// </summary>
public sealed record InstallOutcome(bool Success, string? Error);

/// <summary>
/// The single install/uninstall driver shared by both stamped entry points —
/// the console <c>SigilBuild.Wrapper</c> and the Avalonia
/// <c>SigilBuild.Installer.Host</c>. It loads the embedded <see cref="WrapperBlob"/>,
/// parses argv through the one <see cref="CommandLineParser"/>, and exposes:
/// <list type="bullet">
///   <item><description><see cref="RunHeadlessAsync"/> for <c>/silent</c> / <c>/verysilent</c> and non-interactive modes;</description></item>
///   <item><description><see cref="RunInstallAsync"/> for the GUI wizard's Installing screen (drives the same engine with an <see cref="IProgress{T}"/> adapter);</description></item>
/// </list>
/// On a successful install both paths run the same completion step (persist the
/// rollback journal + write the ARP entry) via <see cref="PersistCompletion"/>.
/// </summary>
public sealed class InstallSession
{
    private readonly WrapperBlob _blob;
    private readonly ParsedCommandLine _parsed;

    private InstallSession(WrapperBlob blob, ParsedCommandLine parsed)
    {
        _blob = blob;
        _parsed = parsed;
    }

    /// <summary>
    /// Build a session for the running exe: read the embedded blob, then parse
    /// <paramref name="args"/> against its parameter schema.
    /// </summary>
    /// <exception cref="UsageException">
    /// Bad flag / undeclared parameter, or — in silent install mode — a required
    /// parameter (no default) left unset.
    /// </exception>
    public static InstallSession Create(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var blob = WrapperBlob.LoadFromSelf();
        var parsed = CommandLineParser.Parse(args, blob.Parameters);
        return new InstallSession(blob, parsed);
    }

    /// <summary>
    /// Build a session directly from an in-memory blob + parsed command line,
    /// bypassing <see cref="WrapperBlob.LoadFromSelf"/>. Test-only seam: lets a
    /// test drive the real install lifecycle (payload extraction → engine →
    /// temp cleanup) with a synthesised blob instead of a stamped exe.
    /// </summary>
    internal static InstallSession ForTesting(WrapperBlob blob, ParsedCommandLine parsed)
    {
        ArgumentNullException.ThrowIfNull(blob);
        ArgumentNullException.ThrowIfNull(parsed);
        return new InstallSession(blob, parsed);
    }

    /// <summary>The requested operating mode (install / update / uninstall).</summary>
    public WrapperMode Mode => _parsed.Mode;

    /// <summary>True when <c>/silent</c>, <c>/S</c>, or <c>/verysilent</c> was supplied.</summary>
    public bool Silent => _parsed.Silent;

    /// <summary>True when <c>/verysilent</c> was supplied.</summary>
    public bool VerySilent => _parsed.VerySilent;

    /// <summary>The install's application id (the ARP subkey / state-store key).</summary>
    public string AppId => _blob.AppId;

    /// <summary>The declared install-time parameter schema (for the wizard's Configure screens, Task T9).</summary>
    public IReadOnlyList<ParameterDefinition> Parameters => _blob.Parameters;

    /// <summary>The full parsed command line (overrides, install-dir, scope) for GUI defaults.</summary>
    public ParsedCommandLine CommandLine => _parsed;

    /// <summary>
    /// Run to completion without any UI. Routes by mode, echoing the engine's
    /// log lines to <paramref name="output"/>. Returns the process exit code:
    /// <c>0</c> ok, <c>1</c> step failure (rolled back), <c>2</c> cancelled
    /// (rolled back), <c>64</c> unsupported mode.
    /// </summary>
    public async Task<int> RunHeadlessAsync(TextWriter output, TextWriter error, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        switch (_parsed.Mode)
        {
            case WrapperMode.Update:
                // Update is not implemented in this track. Say so explicitly and
                // exit 64 rather than silently running the (always-empty)
                // WrapperBlob.UpdateSteps and reporting success.
                error.WriteLine(
                    "/Update is not supported by this installer: update_steps run via the delta-update SDK, not the setup runtime.");
                return 64;

            case WrapperMode.Uninstall:
                return await RunUninstallAsync(error, ct).ConfigureAwait(false);

            case WrapperMode.Install:
            default:
                var progress = new WriterProgress(output);
                try
                {
                    var outcome = await RunInstallAsync(progress, ct).ConfigureAwait(false);
                    if (outcome.Success)
                    {
                        return 0;
                    }
                    if (outcome.Error is not null)
                    {
                        error.WriteLine(outcome.Error);
                    }
                    return 1;
                }
                catch (OperationCanceledException)
                {
                    // Rollback already ran inside the engine before the throw.
                    return 2;
                }
        }
    }

    /// <summary>
    /// Run the install pipeline (pre → install → post) under a single rollback
    /// journal, forwarding <paramref name="progress"/> to the engine, and — on
    /// success — persist the journal + write the ARP entry. A step failure
    /// returns <c>Success=false</c> after the engine has rolled back;
    /// cancellation propagates as <see cref="OperationCanceledException"/> after
    /// the engine has rolled back.
    /// </summary>
    // Wizard-collected parameter values (T9), bound into the engine context on the
    // next RunInstall* call. Null on the console/silent path. Set once, before the
    // single install run — the host is a single-install process.
    private IReadOnlyDictionary<string, string>? _collectedValues;

    public Task<InstallOutcome> RunInstallAsync(IProgress<StepProgress>? progress, CancellationToken ct = default)
        => RunInstallCoreAsync(WrapperBlob.LoadPayloadBytes(), progress, ct);

    /// <summary>
    /// GUI entry point (T9): run the install pipeline binding the wizard-collected
    /// parameter values into <c>param.*</c> / <c>parameters.*</c> for the engine,
    /// so a step <c>when: "param.autostart == true"</c> observes what the user
    /// picked on the custom screens. Values are keyed by canonical parameter name;
    /// they take precedence over CLI <c>/P</c> overrides and schema defaults.
    /// </summary>
    public Task<InstallOutcome> RunInstallAsync(
        IReadOnlyDictionary<string, string>? collectedValues,
        IProgress<StepProgress>? progress,
        CancellationToken ct = default)
    {
        _collectedValues = collectedValues;
        return RunInstallCoreAsync(WrapperBlob.LoadPayloadBytes(), progress, ct);
    }

    /// <summary>
    /// Core install lifecycle shared by every entry path: extract the embedded
    /// payload into a fresh temp dir, run the pre → install → post pipeline with
    /// the payload root threaded into the <see cref="StepContext"/> (so
    /// <c>payload://</c> sources resolve), and — on success — persist the journal
    /// and register ARP. The temp dir is removed in the <c>finally</c>,
    /// guaranteeing no <c>%TEMP%</c> leak on success, step failure,
    /// cancellation, or rollback.
    /// </summary>
    /// <remarks>
    /// <paramref name="payloadBytes"/> is passed in (rather than read here) so a
    /// test can exercise the full lifecycle — including cleanup — without a
    /// stamped exe; the public <see cref="RunInstallAsync"/> supplies the real
    /// <see cref="WrapperBlob.LoadPayloadBytes"/> result. An empty array (the
    /// un-stamped runtime) skips extraction and leaves the payload root
    /// <c>null</c>.
    /// </remarks>
    internal async Task<InstallOutcome> RunInstallCoreAsync(
        byte[] payloadBytes,
        IProgress<StepProgress>? progress,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payloadBytes);

        PayloadExtraction? payload = null;
        try
        {
            string? payloadRoot = null;
            if (payloadBytes.Length > 0)
            {
                payload = PayloadExtraction.Extract(payloadBytes, _blob.AppId);
                payloadRoot = payload.Root;
            }

            var ctx = StepContext.From(_blob, _parsed, payloadRoot, _collectedValues);
            var result = await new InstallEngine().RunAsync(
                preInstall: _blob.PreInstall,
                installSteps: _blob.InstallSteps,
                postInstall: _blob.PostInstall,
                ctx: ctx,
                ct: ct,
                progress: progress).ConfigureAwait(false);

            if (!result.Success)
            {
                return new InstallOutcome(false, result.Error);
            }

            PersistCompletion(result.Journal, ctx.SecretValues);
            return new InstallOutcome(true, null);
        }
        finally
        {
            // Runs on success, step-failure return, and the OperationCanceledException
            // rethrow — so the extracted payload temp dir never leaks.
            payload?.Dispose();
        }
    }

    /// <summary>
    /// Shared install-completion helper: on a successful install, snapshot the
    /// rollback journal to the state store and register the ARP entry so the
    /// app appears in Add/Remove Programs. Used by both the console and GUI
    /// paths.
    /// </summary>
    /// <remarks>
    /// The DisplayName/Version/Publisher/size values are the acknowledged
    /// placeholders (Task T10 threads the real <c>manifest.App.*</c> fields and
    /// the packed size through the blob). No-ops for an un-stamped runtime (the
    /// dev/smoke <see cref="WrapperBlob.Empty"/>) and off Windows.
    /// </remarks>
    private void PersistCompletion(
        RollbackJournal journal,
        IReadOnlyList<string> secretValues)
    {
        if (ReferenceEquals(_blob, WrapperBlob.Empty))
        {
            return; // un-stamped runtime: nothing real to register.
        }
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Redact any secret value from the persisted uninstall state (decision 6).
        UninstallStateStore.Save(_blob.AppId, journal, secretValues);
        ArpRegistration.Register(new ArpRegistration.Entry(
            AppId: _blob.AppId,
            DisplayName: _blob.AppId,
            DisplayVersion: "1.0.0",
            Publisher: "Unknown",
            UninstallString: ArpRegistration.BuildUninstallString(Environment.ProcessPath ?? "."),
            EstimatedSizeBytes: 0));
    }

    private async Task<int> RunUninstallAsync(TextWriter error, CancellationToken ct)
    {
        var result = await new UninstallEngine().RunAsync(_blob.AppId, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            if (result.Error is not null)
            {
                error.WriteLine(result.Error);
            }
            return 1;
        }
        return 0;
    }

    /// <summary>
    /// Synchronous <see cref="IProgress{T}"/> that echoes each non-null log line
    /// to a <see cref="TextWriter"/> — used by the headless path so console
    /// output stays ordered (unlike <see cref="Progress{T}"/>, which marshals to
    /// a captured <see cref="SynchronizationContext"/>).
    /// </summary>
    private sealed class WriterProgress : IProgress<StepProgress>
    {
        private readonly TextWriter _writer;

        public WriterProgress(TextWriter writer) => _writer = writer;

        public void Report(StepProgress value)
        {
            if (value.Message is not null)
            {
                _writer.WriteLine(value.Message);
            }
        }
    }
}
