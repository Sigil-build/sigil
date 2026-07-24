using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Cli;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Tests.Helpers;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Engine;

/// <summary>
/// P7 (gap G8): /LOG install logging. Covers the sink itself
/// (<see cref="InstallLog"/>), and end-to-end session runs (silent + headed,
/// success + forced failure) asserting the log is created at the explicit and
/// default paths, carries the step / rollback / exit-code trail, and redacts
/// secrets.
/// </summary>
public sealed class InstallLoggingTests
{
    // ── The sink ──────────────────────────────────────────────────────────────

    [Fact]
    public void InstallLog_writes_timestamped_lines_and_redacts_secrets()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "sink.log");

        var log = InstallLog.TryOpen(path);
        log.Should().NotBeNull();
        log!.SetSecrets(new[] { "SEKRET-TOKEN" });
        log.WriteLine("connecting with SEKRET-TOKEN now");
        log.WriteLine("plain line");

        var content = File.ReadAllText(path);
        content.Should().NotContain("SEKRET-TOKEN", "the sink redacts before writing");
        content.Should().Contain("***");
        content.Should().Contain("plain line");
        content.Should().MatchRegex(@"\[\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z\] ");
    }

    [Fact]
    public void InstallLog_TryOpen_returns_null_for_blank_path()
    {
        InstallLog.TryOpen("").Should().BeNull();
        InstallLog.TryOpen("   ").Should().BeNull();
    }

    // ── Silent install: explicit + default paths, success + exit code ─────────

    [Fact]
    public async Task Silent_install_writes_log_at_explicit_path_with_exit_code()
    {
        using var tmp = new TempDir();
        var logPath = Path.Combine(tmp.Path, "explicit.log");

        // Un-stamped runtime → Empty blob → empty pipeline → exits 0, no ARP writes.
        var session = InstallSession.Create(new[] { "/silent", $"/LOG={logPath}" });
        var code = await session.RunHeadlessAsync(new StringWriter(), new StringWriter());

        code.Should().Be(0);
        File.Exists(logPath).Should().BeTrue();
        var log = File.ReadAllText(logPath);
        log.Should().Contain("=== sigil install log");
        log.Should().Contain("result: success");
        log.Should().Contain("exit code: 0");
        session.LogFilePath.Should().Be(Path.GetFullPath(logPath));
    }

    [Fact]
    public async Task Bare_LOG_writes_to_the_default_temp_path()
    {
        // Create() yields the un-stamped Empty blob (AppId "<unset>") → the default
        // log path sanitizes to sigil-_unset_.log under %TEMP%.
        var defaultPath = Path.Combine(Path.GetTempPath(), "sigil-_unset_.log");
        SafeDelete(defaultPath);
        try
        {
            var session = InstallSession.Create(new[] { "/silent", "/LOG" });
            session.LogFilePath.Should().Be(defaultPath);

            var code = await session.RunHeadlessAsync(new StringWriter(), new StringWriter());

            code.Should().Be(0);
            File.Exists(defaultPath).Should().BeTrue();
            File.ReadAllText(defaultPath).Should().Contain("exit code: 0");
        }
        finally
        {
            SafeDelete(defaultPath);
        }
    }

    [Fact]
    public async Task Headed_install_run_writes_the_log_too()
    {
        using var tmp = new TempDir();
        var logPath = Path.Combine(tmp.Path, "headed.log");

        // The GUI entry point (no /silent) drives the same session; the log is
        // produced for the wizard path as well as the silent path.
        var session = InstallSession.Create(new[] { $"/LOG={logPath}" });
        var outcome = await session.RunInstallAsync(new NullProgress(), CancellationToken.None);

        outcome.Success.Should().BeTrue();
        File.Exists(logPath).Should().BeTrue();
        File.ReadAllText(logPath).Should().Contain("result: success");
    }

    // ── Forced step failure: failing step + rollback trail + exit code ────────

    [Fact]
    public async Task Forced_step_failure_log_has_failing_step_rollback_and_exit_code()
    {
        using var tmp = new TempDir();
        var logPath = Path.Combine(tmp.Path, "failure.log");

        var blob = new WrapperBlob(
            AppId: "com.acme.LogFailTest",
            Parameters: Array.Empty<ParameterDefinition>(),
            InstallSteps: new InstallStep[]
            {
                // Step 1 succeeds and journals a RemoveDirectory (fresh dir)…
                new InstallStep.DirectoryCreate("mk", Path.Combine(tmp.Path, "sub"), When: null, OnFailure.Rollback),
                // …step 2 fails (glob root does not exist) → rollback replays step 1.
                new InstallStep.FileCopy(
                    "cp",
                    Path.Combine(tmp.Path, "does-not-exist-xyz", "*"),
                    Path.Combine(tmp.Path, "dst"),
                    Overwrite: false, When: null, OnFailure.Rollback),
            },
            PreInstall: Array.Empty<InstallStep>(),
            PostInstall: Array.Empty<InstallStep>(),
            UpdateSteps: Array.Empty<InstallStep>());

        var parsed = CommandLineParser.Parse(new[] { "/silent", $"/LOG={logPath}" }, blob.Parameters);
        var session = InstallSession.ForTesting(blob, parsed);

        var code = await session.RunHeadlessAsync(new StringWriter(), new StringWriter());

        code.Should().Be(1);
        var log = File.ReadAllText(logPath);
        log.Should().Contain("mkdir", "the successful step is logged");
        log.Should().Contain("step 'cp' failed", "the failing step is named");
        log.Should().Contain("rollback: reverting changes");
        log.Should().Contain("rmdir", "the rollback trail lists each reversal");
        log.Should().Contain("exit code: 1");
    }

    // ── P10 (gap G11): a locked component ignores an override, and logs it ────

    [Fact]
    public async Task Locked_component_override_attempt_is_ignored_and_logged()
    {
        using var tmp = new TempDir();
        var logPath = Path.Combine(tmp.Path, "locked.log");
        var gatedDir = Path.Combine(tmp.Path, "gated");

        var blob = new WrapperBlob(
            AppId: "com.acme.LockedOptionTest",
            Parameters: Array.Empty<ParameterDefinition>(),
            InstallSteps: new InstallStep[]
            {
                // Gated on a LOCKED option whose default is true → the step must run
                // even though the CLI tried to force the option off.
                new InstallStep.DirectoryCreate(
                    "mk", gatedDir, When: "option.add_to_path", OnFailure.Fail),
            },
            PreInstall: Array.Empty<InstallStep>(),
            PostInstall: Array.Empty<InstallStep>(),
            UpdateSteps: Array.Empty<InstallStep>(),
            Options: new[] { new InstallerOptionComponent("add_to_path", Default: true, Locked: true) });

        var parsed = CommandLineParser.Parse(
            new[] { "/silent", "/Padd_to_path=false", $"/LOG={logPath}" }, blob.Parameters);
        var session = InstallSession.ForTesting(blob, parsed);

        var code = await session.RunHeadlessAsync(new StringWriter(), new StringWriter());

        code.Should().Be(0);
        Directory.Exists(gatedDir).Should().BeTrue("a locked component stays at its default (on) despite the override");
        var log = File.ReadAllText(logPath);
        log.Should().Contain("add_to_path", "the ignored override is logged by component name");
        log.Should().Contain("locked", "the log states the override was ignored because the component is locked");
    }

    // ── Secret redaction reaches the log file even via a resolved error path ──

    [Fact]
    public async Task Secret_resolved_into_a_step_error_is_redacted_in_the_log()
    {
        const string Secret = "LK-9999-TOPSECRET";
        using var tmp = new TempDir();
        var logPath = Path.Combine(tmp.Path, "secret.log");

        var blob = new WrapperBlob(
            AppId: "com.acme.LogSecretTest",
            Parameters: new[]
            {
                new ParameterDefinition("license_key", ParameterType.Secret, null, null, true, LocalizedText.Plain("k"), null, null, null),
            },
            InstallSteps: new InstallStep[]
            {
                // The copy source embeds the secret; the (non-existent) glob root is
                // surfaced verbatim in the step's error → the resolved secret would
                // reach the log unless redacted.
                new InstallStep.FileCopy(
                    "cp",
                    Path.Combine(tmp.Path, "${parameters.license_key}", "*"),
                    Path.Combine(tmp.Path, "dst"),
                    Overwrite: false, When: null, OnFailure.Rollback),
            },
            PreInstall: Array.Empty<InstallStep>(),
            PostInstall: Array.Empty<InstallStep>(),
            UpdateSteps: Array.Empty<InstallStep>());

        var parsed = CommandLineParser.Parse(
            new[] { "/silent", $"/Plicense_key={Secret}", $"/LOG={logPath}" }, blob.Parameters);
        var session = InstallSession.ForTesting(blob, parsed);

        var code = await session.RunHeadlessAsync(new StringWriter(), new StringWriter());

        code.Should().Be(1);
        var log = File.ReadAllText(logPath);
        log.Should().NotContain(Secret, "no log line may leak a secret value");
        log.Should().Contain("***", "the secret occurrence in the error is redacted");
    }

    // ── /Update honors the same flags (T12.4) ─────────────────────────────────

    [Fact]
    public async Task Update_honors_LOG_flag_and_records_the_update_stage_and_exit_code()
    {
        using var tmp = new TempDir();
        var logPath = Path.Combine(tmp.Path, "update.log");

        // The un-stamped runtime carries no updates: metadata, so this exercises
        // the "not update-enabled" stage — the one /Update stage InstallSession can
        // reach without live network I/O (RunUpdateAsync wires PRODUCTION HTTP/
        // download/launch seams with no test seam — see UpdateRunner's remarks).
        // The deeper "checking for updates" / "up to date" / "downloading" stages
        // are covered directly against UpdateRunner (with fakes) in
        // UpdateRunnerTests, which already assert their exact log content.
        var session = InstallSession.Create(new[] { "/Update", "/silent", $"/LOG={logPath}" });
        var code = await session.RunHeadlessAsync(new StringWriter(), new StringWriter());

        code.Should().Be(InstallSession.UpdateNotConfiguredExitCode);
        File.Exists(logPath).Should().BeTrue();
        var log = File.ReadAllText(logPath);
        log.Should().Contain("=== sigil update log", "the /LOG header names the update mode");
        log.Should().Contain("not update-enabled", "UpdateRunner's report callback writes each stage into the /LOG sink too");
        log.Should().Contain($"exit code: {InstallSession.UpdateNotConfiguredExitCode}");
    }

    // ── Uninstall honors the same flags ──────────────────────────────────────

    [Fact]
    public async Task Uninstall_honors_LOG_flag()
    {
        using var tmp = new TempDir();
        var logPath = Path.Combine(tmp.Path, "uninstall.log");

        // No install state for the Empty blob's "<unset>" app → uninstall exits 1,
        // but the log is still written with the uninstall header + exit code.
        var session = InstallSession.Create(new[] { "/Uninstall", $"/LOG={logPath}" });
        var code = await session.RunHeadlessAsync(new StringWriter(), new StringWriter());

        code.Should().Be(1);
        File.Exists(logPath).Should().BeTrue();
        var log = File.ReadAllText(logPath);
        log.Should().Contain("=== sigil uninstall log");
        log.Should().Contain("exit code: 1");
    }

    private static void SafeDelete(string path)
    {
#pragma warning disable CA1031 // test cleanup best-effort
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort */ }
#pragma warning restore CA1031
    }

    private sealed class NullProgress : IProgress<StepProgress>
    {
        public void Report(StepProgress value) { }
    }
}
