using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
/// Decision 6 / T9 secret hygiene: a <see cref="ParameterType.Secret"/> value must
/// never reach the on-screen/console log lines or the persisted uninstall state
/// (journal). These tests grep both surfaces for a known secret and assert it is
/// absent (redacted to <c>***</c>).
/// </summary>
public sealed class SecretHygieneTests
{
    private const string Secret = "LK-9999-TOPSECRET";

    private static ParameterDefinition LicenseKey() =>
        new("license_key", ParameterType.Secret, null, null, true, LocalizedText.Plain("License key"), null, null, null);

    private static WrapperBlob BlobWith(params InstallStep[] steps) =>
        new(
            AppId: "com.acme.Studio",
            Parameters: new[] { LicenseKey() },
            InstallSteps: steps,
            PreInstall: Array.Empty<InstallStep>(),
            PostInstall: Array.Empty<InstallStep>(),
            UpdateSteps: Array.Empty<InstallStep>());

    [Fact]
    public void StepContext_collects_and_redacts_secret_values()
    {
        var blob = BlobWith();
        var parsed = CommandLineParser.Parse(new[] { $"/Plicense_key={Secret}" }, blob.Parameters);

        var ctx = StepContext.From(blob, parsed);

        ctx.SecretValues.Should().Contain(Secret);
        ctx.Redact($"activating with {Secret} now").Should().Be("activating with *** now");
    }

    [Fact]
    public async Task Secret_value_is_absent_from_journal_and_log_output()
    {
        using var tmp = new TempDir();
        // A step whose resolved path embeds the secret — so the rollback journal's
        // RemoveDirectory record would carry it verbatim if not redacted.
        var dir = tmp.Path + "/app-${parameters.license_key}";
        // R16: an OS temp directory is never install_dir, so the out-of-tree write
        // is declared with the production per-step opt-out. Under test here is
        // secret redaction in the journal and the log.
        var blob = BlobWith(new InstallStep.DirectoryCreate("mk", dir, When: null, OnFailure.Fail)
        { AllowOutsideInstallDir = true });
        var parsed = CommandLineParser.Parse(new[] { $"/Plicense_key={Secret}" }, blob.Parameters);
        var ctx = StepContext.From(blob, parsed);

        var events = new List<StepProgress>();
        var progress = new ListProgress(events);

        var result = await new InstallEngine().RunAsync(
            Array.Empty<InstallStep>(), blob.InstallSteps, Array.Empty<InstallStep>(),
            ctx, progress, CancellationToken.None);

        result.Success.Should().BeTrue();
        Directory.Exists(Path.Combine(tmp.Path, $"app-{Secret}")).Should().BeTrue("the step should have run");

        // --- Grep the captured log output ---
        var log = string.Join("\n", events.Where(e => e.Message is not null).Select(e => e.Message));
        log.Should().NotContain(Secret, "no log line may leak a secret value");

        // --- Grep the persisted journal / uninstall state ---
        var appId = "t9-secret-" + Guid.NewGuid().ToString("N");
        try
        {
            UninstallStateStore.Save(appId, result.Journal, InstallScope.User, ctx.SecretValues);
            var content = File.ReadAllText(UninstallStateStore.PathFor(appId, InstallScope.User));
            content.Should().NotContain(Secret, "the persisted journal must never contain a secret value");
            content.Should().Contain("***", "the secret occurrence in the journal must be redacted");
        }
        finally
        {
            UninstallStateStore.Delete(appId, InstallScope.User);
        }
    }

    [Fact]
    public async Task Secret_derived_var_is_absent_from_journal_and_log_output()
    {
        using var tmp = new TempDir();

        // P1: a var derives from the secret param, and a step path references it as
        // {var.tainted}. The resolved directory embeds the secret, so both the log
        // and the persisted journal would leak it unless the var inherited
        // secretness (ADR-008 §3) and the value is redacted end-to-end.
        var dir = tmp.Path + "/app-{var.tainted}";
        var blob = new WrapperBlob(
            AppId: "com.acme.Studio",
            Parameters: new[] { LicenseKey() },
            // R16: an OS temp directory is never install_dir — see the note above.
            // Note the {var.tainted} token must still RESOLVE, opt-out or not.
            InstallSteps: new InstallStep[]
            {
                new InstallStep.DirectoryCreate("mk", dir, When: null, OnFailure.Fail)
                    { AllowOutsideInstallDir = true },
            },
            PreInstall: Array.Empty<InstallStep>(),
            PostInstall: Array.Empty<InstallStep>(),
            UpdateSteps: Array.Empty<InstallStep>(),
            Vars: new[] { new InstallerVar("tainted", "param.license_key") });

        var parsed = CommandLineParser.Parse(new[] { $"/Plicense_key={Secret}" }, blob.Parameters);
        var ctx = StepContext.From(blob, parsed);

        // The secret-derived var carries the secret value AND is registered for redaction.
        ctx.SecretValues.Should().Contain(Secret, "a var derived from a secret param inherits secretness");
        ctx.Redact($"landing at {Secret}").Should().Be("landing at ***");

        var events = new List<StepProgress>();
        var result = await new InstallEngine().RunAsync(
            Array.Empty<InstallStep>(), blob.InstallSteps, Array.Empty<InstallStep>(),
            ctx, new ListProgress(events), CancellationToken.None);

        result.Success.Should().BeTrue();
        Directory.Exists(Path.Combine(tmp.Path, $"app-{Secret}"))
            .Should().BeTrue("the {var.tainted} token must expand to the secret-derived value");

        var log = string.Join("\n", events.Where(e => e.Message is not null).Select(e => e.Message));
        log.Should().NotContain(Secret, "no log line may leak a secret-derived var value");

        var appId = "p1-secret-" + Guid.NewGuid().ToString("N");
        try
        {
            UninstallStateStore.Save(appId, result.Journal, InstallScope.User, ctx.SecretValues);
            var content = File.ReadAllText(UninstallStateStore.PathFor(appId, InstallScope.User));
            content.Should().NotContain(Secret, "the persisted journal must never contain a secret-derived value");
            content.Should().Contain("***", "the secret occurrence in the journal must be redacted");
        }
        finally
        {
            UninstallStateStore.Delete(appId, InstallScope.User);
        }
    }

    // ── P11 system steps: journal records carry only name/path — never a
    // resolved secret. schtasks/netsh/COM all journal their inverse BEFORE the
    // native/process call (see each step's own "no secrets" doc comment); these
    // three tests drive the SAME redaction path (UninstallStateStore.RedactSecrets)
    // the DirectoryCreate tests above cover, but through the actual P11 step
    // types so the P11 journal surfaces are directly exercised, not just
    // inferred from a generic mechanism. Each step's own unit tests
    // (ScheduledTaskCreateStepTests / FirewallRuleStepTests /
    // ComRegisterStepTests) already establish that running these steps
    // unelevated is safe locally (the journal append happens before the native
    // call, so a non-admin sandbox never actually mutates system state).
    //
    // No log/progress assertion here (unlike the DirectoryCreate tests above):
    // InstallEngine.Describe has no arm for ScheduledTaskCreate / FirewallRule /
    // ComRegister, so on the success/OnFailure.Continue path (used below) the
    // reported progress line falls to Describe's default, `spec.Id` — the
    // manifest-declared step id ("t"/"fw"/"reg"), never a resolved field. A
    // `log.Should().NotContain(Secret)` assertion here would be vacuously true
    // regardless of whether redaction works, so it is intentionally omitted;
    // the journal assertion below is the one that actually exercises secret
    // redaction for these three step types.

    [Fact]
    public void ScheduledTaskCreate_secret_in_name_is_absent_from_journal()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // R3/R9 changed how this record can be produced. `scheduled_task_create`
        // now refuses a program that is not anchored in an admin-only-writable
        // install_dir, so driving the step far enough to journal would mean
        // anchoring it somewhere admin-only — and then schtasks.exe DOES run, with
        // /RU SYSTEM, creating a live SYSTEM task named after the secret on any
        // elevated runner. The record is therefore appended directly. The property
        // under test is unchanged and still exercises exactly the surface it
        // always did: UninstallStateStore's redaction over a DeleteScheduledTask
        // record whose task name came out of ctx.Resolve. That the step appends
        // this record before it launches schtasks is asserted end-to-end by
        // ScheduledTaskCreateInstallTests on the CI VM.
        var blob = BlobWith();
        var parsed = CommandLineParser.Parse(new[] { $"/Plicense_key={Secret}" }, blob.Parameters);
        var ctx = StepContext.From(blob, parsed);

        var journal = new RollbackJournal();
        journal.Append(new RollbackRecord.DeleteScheduledTask(
            ctx.Resolve("SigilTestTask_${parameters.license_key}")));

        var appId = "t11-1-secret-" + Guid.NewGuid().ToString("N");
        try
        {
            UninstallStateStore.Save(appId, journal, InstallScope.User, ctx.SecretValues);
            var content = File.ReadAllText(UninstallStateStore.PathFor(appId, InstallScope.User));
            content.Should().NotContain(Secret, "the persisted journal must never contain a secret value");
            content.Should().Contain("***", "the secret occurrence in the journal must be redacted");
        }
        finally
        {
            UninstallStateStore.Delete(appId, InstallScope.User);
        }
    }

    [Fact]
    public async Task FirewallRule_secret_in_name_is_absent_from_journal()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var blob = BlobWith(new InstallStep.FirewallRule(
            "fw", "SigilTestRule_${parameters.license_key}", "in", "allow",
            Program: null, Port: null, Protocol: null, When: null, OnFailure: OnFailure.Continue));
        var parsed = CommandLineParser.Parse(new[] { $"/Plicense_key={Secret}" }, blob.Parameters);
        var ctx = StepContext.From(blob, parsed);

        var result = await new InstallEngine().RunAsync(
            Array.Empty<InstallStep>(), blob.InstallSteps, Array.Empty<InstallStep>(),
            ctx, progress: null, CancellationToken.None);

        var appId = "t11-3-secret-" + Guid.NewGuid().ToString("N");
        try
        {
            UninstallStateStore.Save(appId, result.Journal, InstallScope.User, ctx.SecretValues);
            var content = File.ReadAllText(UninstallStateStore.PathFor(appId, InstallScope.User));
            content.Should().NotContain(Secret, "the persisted journal must never contain a secret value");
            content.Should().Contain("***", "the secret occurrence in the journal must be redacted");
        }
        finally
        {
            UninstallStateStore.Delete(appId, InstallScope.User);
        }
    }

    [Fact]
    public async Task ComRegister_secret_in_path_is_absent_from_journal()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // A non-existent path (LoadFailed) — safe to run locally without admin
        // or a real self-registering DLL (mirrors ComRegisterStepTests), and safe
        // on an elevated runner too: nothing is ever registered.
        //
        // R3/R9: the DLL path is now anchored to install_dir and must sit in an
        // admin-only-writable directory, so the run is machine-scope into
        // %ProgramFiles%\Common Files — a real admin-only directory — with a file
        // that does not exist in it. The step is admitted, journals its inverse,
        // and then LoadLibraryEx fails, which is exactly the arrangement this test
        // has always used.
        var blob = BlobWith(new InstallStep.ComRegister(
            "reg", @"{install_dir}\${parameters.license_key}.dll", When: null, OnFailure: OnFailure.Continue));
        var parsed = CommandLineParser.Parse(new[] { $"/Plicense_key={Secret}" }, blob.Parameters);
        var ctx = StepContext.From(
            blob,
            parsed,
            scope: InstallScope.Machine,
            collectedInstallDir: Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Common Files"));

        var result = await new InstallEngine().RunAsync(
            Array.Empty<InstallStep>(), blob.InstallSteps, Array.Empty<InstallStep>(),
            ctx, progress: null, CancellationToken.None);

        var appId = "t11-2-secret-" + Guid.NewGuid().ToString("N");
        try
        {
            UninstallStateStore.Save(appId, result.Journal, InstallScope.User, ctx.SecretValues);
            var content = File.ReadAllText(UninstallStateStore.PathFor(appId, InstallScope.User));
            content.Should().NotContain(Secret, "the persisted journal must never contain a secret value");
            content.Should().Contain("***", "the secret occurrence in the journal must be redacted");
        }
        finally
        {
            UninstallStateStore.Delete(appId, InstallScope.User);
        }
    }

    private sealed class ListProgress : IProgress<StepProgress>
    {
        private readonly List<StepProgress> _sink;
        public ListProgress(List<StepProgress> sink) => _sink = sink;
        public void Report(StepProgress value) => _sink.Add(value);
    }
}
