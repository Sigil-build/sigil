namespace SigilBuild.Wrapper.Tests.Steps;

using System;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Cli;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Steps;
using SigilBuild.Wrapper.Tests.Helpers;

/// <summary>
/// Register rows R3 and R9: the four steps that hand a manifest-supplied path to
/// something running with SYSTEM-level authority — <c>scheduled_task_create</c>
/// (<c>/RU SYSTEM</c>), <c>service_install</c>, <c>com_register</c>
/// (<c>LoadLibrary</c> inside the elevated installer) and <c>firewall_rule</c>.
/// Each must refuse a target that is not anchored inside <c>install_dir</c> and
/// not sited in an admin-only-writable directory.
/// </summary>
/// <remarks>
/// <para>
/// <b>No test in this file can create a scheduled task, a service, a COM
/// registration or a firewall rule.</b> Every case here is a REFUSAL case: the
/// guard returns before <c>schtasks.exe</c> / <c>sc.exe</c> / <c>netsh.exe</c> is
/// started and before <c>LoadLibraryEx</c> is called, and each test asserts the
/// rollback journal is still empty — which is only true if the step returned
/// before the code that mutates anything. The accept side is proved against the
/// pure <see cref="PrivilegedTargetGuard"/> seam in
/// <c>PrivilegedTargetGuardTests</c>, precisely so no test ever runs one of these
/// steps to completion on an elevated CI runner.
/// </para>
/// <para>
/// Both entry paths are covered. Lane S2 has twice shipped a rule that was
/// correct on the silent path and broken on the headed one, because <c>/D=</c>
/// arrives as <c>ParsedCommandLine.InstallDir</c> while the wizard's value
/// arrives as <c>collectedInstallDir</c>. The guard anchors on
/// <c>ctx.InstallDir</c>, so the fields are proved to converge here rather than
/// assumed to.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class PrivilegedStepContainmentTests
{
    private const string EvilProgram = @"C:\Users\Public\evil.exe";

    /// <summary>
    /// An existing, admin-only-writable machine directory to anchor on. Real ACLs
    /// are read, not simulated; <see cref="AssertAnchorIsAdminOnly"/> fails loudly
    /// if this machine disagrees rather than letting the test pass vacuously.
    /// </summary>
    private static string MachineAnchor =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Common Files");

    // ── scheduled_task_create (/RU SYSTEM) ────────────────────────────────────

    [WindowsFact("Windows ACL APIs")]
    public async Task Scheduled_task_refuses_a_program_outside_the_install_dir()
    {
        // Setup.exe /allusers /D=C:\Program Files\App, manifest points the task at
        // a world-writable directory: a SYSTEM task pointing at a binary any user
        // can replace. The step must refuse before schtasks.exe is started.
        var ctx = AnchoredContext(MachineAnchor);
        var journal = new RollbackJournal();

        var result = await new ScheduledTaskCreateStep(TaskSpec(EvilProgram))
            .RunAsync(ctx, journal, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("install_dir");
        journal.Records.Should().BeEmpty(
            "the refusal happens before the journal entry, so no DeleteScheduledTask is queued " +
            "for a task this installer never created");
    }

    [WindowsFact("Windows ACL APIs")]
    public async Task Scheduled_task_refuses_a_contained_but_user_writable_program()
    {
        // The check that actually stops the attack: the target IS inside
        // install_dir, but install_dir is a directory this unprivileged user owns.
        using var installDir = new TempDir();
        var ctx = AnchoredContext(installDir.Path);
        var program = Path.Combine(installDir.Path, "heartbeat.exe");
        File.WriteAllText(program, "not really an exe");
        var journal = new RollbackJournal();

        var result = await new ScheduledTaskCreateStep(TaskSpec(program))
            .RunAsync(ctx, journal, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("writable by a non-administrator");
        journal.Records.Should().BeEmpty();
    }

    [WindowsFact("Windows ACL APIs")]
    public async Task Scheduled_task_refuses_a_traversal_escape_from_the_install_dir()
    {
        using var installDir = new TempDir();
        var ctx = AnchoredContext(installDir.Path);
        var journal = new RollbackJournal();

        var result = await new ScheduledTaskCreateStep(TaskSpec(installDir.Path + @"\..\evil.exe"))
            .RunAsync(ctx, journal, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("install_dir");
        journal.Records.Should().BeEmpty();
    }

    [WindowsFact("Windows ACL APIs")]
    public async Task Scheduled_task_refuses_a_run_with_no_resolved_install_dir()
    {
        // Fail closed: with no anchor, no target can be shown safe. Production
        // always resolves one (InstallDirResolver never returns null), so this is
        // only reachable from a hand-built context.
        var journal = new RollbackJournal();

        var result = await new ScheduledTaskCreateStep(TaskSpec(@"C:\Program Files\App\app.exe"))
            .RunAsync(StepContext.Empty, journal, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("no resolved install_dir");
        journal.Records.Should().BeEmpty();
    }

    // ── service_install ───────────────────────────────────────────────────────

    [WindowsFact("Windows ACL APIs")]
    public async Task Service_install_refuses_a_binary_path_outside_the_install_dir()
    {
        var ctx = AnchoredContext(MachineAnchor);
        var journal = new RollbackJournal();
        var spec = new InstallStep.ServiceInstall(
            "svc", "SigilTestService_DoesNotPersist", EvilProgram, "Sigil Test", Description: null,
            StartType: "demand", ServiceAccount: "LocalSystem", StartAfterInstall: false,
            When: null, OnFailure: OnFailure.Continue);

        var result = await new ServiceInstallStep(spec).RunAsync(ctx, journal, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("install_dir");
        result.Error.Should().NotContain("binary not found",
            "an out-of-tree path is a refusal, not a sequencing mistake");
        journal.Records.Should().BeEmpty("sc.exe create must never be reached");
    }

    [WindowsFact("Windows ACL APIs")]
    public async Task Service_install_refuses_a_contained_but_user_writable_binary()
    {
        using var installDir = new TempDir();
        var binary = Path.Combine(installDir.Path, "Updater.exe");
        File.WriteAllText(binary, "not really an exe");
        var journal = new RollbackJournal();
        var spec = new InstallStep.ServiceInstall(
            "svc", "SigilTestService_DoesNotPersist", binary, "Sigil Test", Description: null,
            StartType: "demand", ServiceAccount: "LocalSystem", StartAfterInstall: false,
            When: null, OnFailure: OnFailure.Continue);

        var result = await new ServiceInstallStep(spec)
            .RunAsync(AnchoredContext(installDir.Path), journal, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("writable by a non-administrator");
        journal.Records.Should().BeEmpty();
    }

    // ── com_register (LoadLibrary in the elevated process) ─────────────────────

    [WindowsFact("Windows ACL APIs")]
    public async Task Com_register_refuses_a_dll_outside_the_install_dir()
    {
        var ctx = AnchoredContext(MachineAnchor);
        var journal = new RollbackJournal();
        var spec = new InstallStep.ComRegister(
            "reg", @"C:\Users\Public\evil.dll", When: null, OnFailure: OnFailure.Continue);

        var result = await new ComRegisterStep(spec).RunAsync(ctx, journal, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("install_dir");
        result.Error.Should().NotContain("LoadLibraryEx",
            "the DLL must be refused before it is loaded into the elevated process");
        journal.Records.Should().BeEmpty();
    }

    [WindowsFact("Windows ACL APIs")]
    public async Task Com_register_refuses_a_contained_but_user_writable_dll()
    {
        using var installDir = new TempDir();
        var dll = Path.Combine(installDir.Path, "server.dll");
        File.WriteAllText(dll, "not really a dll");
        var journal = new RollbackJournal();
        var spec = new InstallStep.ComRegister("reg", dll, When: null, OnFailure: OnFailure.Continue);

        var result = await new ComRegisterStep(spec)
            .RunAsync(AnchoredContext(installDir.Path), journal, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("writable by a non-administrator");
        result.Error.Should().NotContain("LoadLibraryEx");
        journal.Records.Should().BeEmpty();
    }

    // ── firewall_rule ─────────────────────────────────────────────────────────

    [WindowsFact("Windows ACL APIs")]
    public async Task Firewall_rule_refuses_a_program_outside_the_install_dir()
    {
        var ctx = AnchoredContext(MachineAnchor);
        var journal = new RollbackJournal();
        var spec = new InstallStep.FirewallRule(
            "fw", "SigilTestRule_DoesNotPersist", "in", "allow",
            Program: EvilProgram, Port: null, Protocol: null,
            When: null, OnFailure: OnFailure.Continue);

        var result = await new FirewallRuleStep(spec).RunAsync(ctx, journal, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("install_dir");
        journal.Records.Should().BeEmpty(
            "netsh must never be reached — not even the idempotency pre-delete, which would " +
            "otherwise remove a same-named rule this installer does not own");
    }

    [WindowsFact("Windows ACL APIs")]
    public async Task Firewall_rule_refuses_a_contained_but_user_writable_program()
    {
        using var installDir = new TempDir();
        var program = Path.Combine(installDir.Path, "app.exe");
        File.WriteAllText(program, "not really an exe");
        var journal = new RollbackJournal();
        var spec = new InstallStep.FirewallRule(
            "fw", "SigilTestRule_DoesNotPersist", "in", "allow",
            Program: program, Port: null, Protocol: null,
            When: null, OnFailure: OnFailure.Continue);

        var result = await new FirewallRuleStep(spec)
            .RunAsync(AnchoredContext(installDir.Path), journal, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("writable by a non-administrator");
        journal.Records.Should().BeEmpty();
    }

    // ── payload:// is refused, deliberately ───────────────────────────────────

    [WindowsFact("Windows ACL APIs")]
    public async Task A_payload_rooted_task_program_is_refused()
    {
        // Register row R9 proposes payload:// as a SAFE source for a privileged
        // target. It is not. PayloadExtraction extracts to
        // %TEMP%\sigil-<appid>-<random>, which under an elevated install is the
        // invoking user's own temp directory — user-writable, therefore
        // replaceable between extraction and use — and InstallSession DELETES it
        // when the run ends, which would leave a SYSTEM task pointing at a path
        // that no longer exists. This pins that the refusal is a decision rather
        // than an accident; the supported shape is file_copy into install_dir
        // first.
        using var payload = new TempDir();
        using var installDir = new TempDir();
        File.WriteAllText(Path.Combine(payload.Path, "heartbeat.exe"), "not really an exe");

        var ctx = new StepContext(
            new System.Collections.Generic.Dictionary<string, object?>(),
            payloadRoot: payload.Path,
            scope: InstallScope.Machine,
            installDir: installDir.Path,
            appId: "com.example.myapp");
        var journal = new RollbackJournal();

        var result = await new ScheduledTaskCreateStep(TaskSpec("payload://heartbeat.exe"))
            .RunAsync(ctx, journal, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("install_dir");
        result.Error.Should().Contain(payload.Path,
            "the message must show the rebased temp path so the author can see what was refused");
        journal.Records.Should().BeEmpty();
    }

    [WindowsFact("Windows ACL APIs")]
    public async Task A_payload_rooted_service_binary_is_rebased_and_then_refused()
    {
        // service_install.binary_path used ctx.Resolve, so 'payload://svc.exe'
        // stayed a LITERAL string: the guard saw scheme text rather than a path,
        // and the author was told the target was outside install_dir without ever
        // learning where it had actually landed. It is a path field and resolves
        // like one now — the message names the rebased temp location, which is
        // what makes the refusal actionable ("file_copy it into install_dir
        // first") rather than mystifying.
        using var payload = new TempDir();
        using var installDir = new TempDir();
        var binary = Path.Combine(payload.Path, "svc.exe");
        File.WriteAllText(binary, "not really an exe");

        var ctx = new StepContext(
            new System.Collections.Generic.Dictionary<string, object?>(),
            payloadRoot: payload.Path,
            scope: InstallScope.Machine,
            installDir: installDir.Path,
            appId: "com.example.myapp");
        var journal = new RollbackJournal();

        var result = await new ServiceInstallStep(
                new InstallStep.ServiceInstall(
                    "svc", "SigilTestService_DoesNotPersist", "payload://svc.exe", "Sigil Test",
                    Description: null, StartType: "demand", ServiceAccount: "LocalSystem",
                    StartAfterInstall: false, When: null, OnFailure: OnFailure.Continue))
            .RunAsync(ctx, journal, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain(binary,
            "binary_path is a path field: the refusal must name the rebased payload location, " +
            "not the literal 'payload://' text");
        result.Error.Should().NotContain("payload://",
            "the scheme must have been resolved away before the guard saw it");
        journal.Records.Should().BeEmpty("sc.exe create must never be reached");
    }

    [WindowsFact("Windows ACL APIs")]
    public void The_payload_root_can_never_satisfy_the_guard()
    {
        // Stated as a property rather than inferred from one arrangement: the
        // extraction root lives under %TEMP%, which is not admin-only writable.
        // So even a run that anchored install_dir ON the payload root would still
        // be refused, by the ACL condition rather than by containment.
        using var payload = new TempDir();

        StateDirectorySecurity.IsAdminOnlyWritable(Path.Combine(payload.Path, "x.exe"))
            .Should().BeFalse("the payload extraction root is the invoking user's temp directory");

        PrivilegedTargetGuard.Check("com_register", "path", payload.Path, Path.Combine(payload.Path, "x.dll"))
            .Should().Contain("writable by a non-administrator");
    }

    // ── Both entry paths: /D= (silent) and wizard-collected (headed) ───────────

    [WindowsFact("Windows scope roots")]
    public async Task Both_entry_paths_refuse_the_same_out_of_tree_task_program()
    {
        var anchor = MachineAnchor;

        var silent = SilentContext(anchor);
        var headed = HeadedContext(anchor);

        silent.InstallDir.Should().Be(anchor, "/D= must carry the install dir on the silent path");
        headed.InstallDir.Should().Be(anchor, "the wizard's collected value must carry it on the headed path");

        foreach (var ctx in new[] { silent, headed })
        {
            var journal = new RollbackJournal();
            var result = await new ScheduledTaskCreateStep(TaskSpec(EvilProgram))
                .RunAsync(ctx, journal, CancellationToken.None);

            result.Success.Should().BeFalse();
            result.Error.Should().Contain("install_dir");
            journal.Records.Should().BeEmpty();
        }
    }

    [WindowsFact("Windows ACL APIs")]
    public void Both_entry_paths_accept_the_same_in_tree_task_program()
    {
        // The accept side is asserted against the guard rather than the step, so
        // that a green elevated CI runner never actually creates a task.
        var anchor = MachineAnchor;
        AssertAnchorIsAdminOnly(anchor);
        var program = Path.Combine(anchor, "heartbeat.exe");

        PrivilegedTargetGuard.Check("scheduled_task_create", "program", SilentContext(anchor).InstallDir, program)
            .Should().BeNull();
        PrivilegedTargetGuard.Check("scheduled_task_create", "program", HeadedContext(anchor).InstallDir, program)
            .Should().BeNull();
    }

    [WindowsFact("Windows ACL APIs")]
    public void A_trailing_separator_on_the_D_override_still_accepts_a_real_descendant()
    {
        // /D=C:\...\ keeps its trailing separator through Path.GetFullPath, so
        // ctx.InstallDir carries it into the guard. PathContainment canonicalizes
        // both ends; without that, the upward walk in IsUnderWithoutTraversal
        // would run past the anchor and refuse a genuine, reparse-free descendant.
        var anchor = MachineAnchor;
        AssertAnchorIsAdminOnly(anchor);

        var ctx = SilentContext(anchor + Path.DirectorySeparatorChar);

        ctx.InstallDir.Should().EndWith(
            Path.DirectorySeparatorChar.ToString(),
            "the trailing separator must survive resolution — that is the shape this regression needs");
        PrivilegedTargetGuard.Check(
                "scheduled_task_create", "program", ctx.InstallDir, Path.Combine(anchor, "heartbeat.exe"))
            .Should().BeNull();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static InstallStep.ScheduledTaskCreate TaskSpec(string program) =>
        new(
            "t1",
            Name: "SigilTestTask_DoesNotPersist",
            Program: program,
            Arguments: null,
            Trigger: "logon",
            RunLevel: "limited",
            When: null,
            OnFailure: OnFailure.Continue);

    /// <summary>A hand-built context anchored on <paramref name="installDir"/>.</summary>
    private static StepContext AnchoredContext(string installDir) =>
        new(new System.Collections.Generic.Dictionary<string, object?>(),
            scope: InstallScope.Machine,
            installDir: installDir,
            appId: "com.example.myapp");

    /// <summary>The silent path: <c>/D=</c> → <c>ParsedCommandLine.InstallDir</c>.</summary>
    private static StepContext SilentContext(string installDir)
    {
        var blob = Blob();
        return StepContext.From(
            blob,
            CommandLineParser.Parse(new[] { "/silent", "/D=" + installDir }, blob.Parameters),
            scope: InstallScope.Machine);
    }

    /// <summary>The headed path: the wizard's Destination screen → <c>collectedInstallDir</c>.</summary>
    private static StepContext HeadedContext(string installDir)
    {
        var blob = Blob();
        return StepContext.From(
            blob,
            CommandLineParser.Parse(Array.Empty<string>(), blob.Parameters),
            payloadRoot: null,
            collected: null,
            scope: InstallScope.Machine,
            collectedOptions: null,
            collectedInstallDir: installDir);
    }

    private static WrapperBlob Blob() =>
        new(
            AppId: "com.example.myapp",
            Parameters: Array.Empty<ParameterDefinition>(),
            InstallSteps: Array.Empty<InstallStep>(),
            PreInstall: Array.Empty<InstallStep>(),
            PostInstall: Array.Empty<InstallStep>(),
            UpdateSteps: Array.Empty<InstallStep>(),
            Scope: InstallScope.Machine,
            Options: null,
            AppName: "MyApp",
            InstallDir: null);

    /// <summary>
    /// Refuse to pass vacuously: if the machine's <c>Common Files</c> is not
    /// admin-only writable, the accept-side assertions below would be proving
    /// nothing, so say so loudly instead.
    /// </summary>
    private static void AssertAnchorIsAdminOnly(string anchor) =>
        StateDirectorySecurity.IsAdminOnlyWritable(Path.Combine(anchor, "probe.exe"))
            .Should().BeTrue(
                $"'{anchor}' must be admin-only writable for the accept-side cases to mean anything");
}
