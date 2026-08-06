namespace SigilBuild.Wrapper.Tests.Engine;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Cli;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Steps;
using SigilBuild.Wrapper.Tests.Helpers;
using Xunit;

/// <summary>
/// Register row R16's headline symptom: an unknown brace token was left literal,
/// so a typo'd <c>installer.vars</c> name silently created a directory called
/// <c>{var.dest}</c> and the install reported success.
/// </summary>
/// <remarks>
/// The check lives in <see cref="StepContext.ResolvePath"/> rather than in the
/// per-step destination guards, so it covers EVERY path-valued step field by
/// construction — including the ones with no containment guard at all
/// (<c>run_program</c>, <c>shortcut_create</c>) and the privileged ones
/// (<c>service_install.binary_path</c>, <c>scheduled_task_create.program</c>).
/// Containment legitimately varies per step and has a manifest opt-out; "this
/// path still contains an unresolved token" never does.
/// </remarks>
public sealed class UnresolvedPathTokenTests
{
    // ── The lexical scanner ───────────────────────────────────────────────────

    [Theory]
    [InlineData(@"C:\App\{var.dest}\x", "var.dest")]
    [InlineData("{install_dir}/app.ini", "install_dir")]
    [InlineData("{scope_root}/x", "scope_root")]
    [InlineData("{temp_dir}/x", "temp_dir")]
    // Lane S3's token. Recognised by SHAPE here; it is absent from the
    // resolution theory below only because it does not exist on this branch yet.
    [InlineData("{staging_dir}/pkg.exe", "staging_dir")]
    [InlineData("{app.name}", "app.name")]
    [InlineData(@"C:\a\{_private}\b", "_private")]
    public void A_surviving_brace_token_is_reported(string value, string expected)
        => BraceTokenScanner.FirstUnresolved(value).Should().Be(expected);

    [Theory]
    [InlineData(@"C:\Program Files\App\app.ini")]
    [InlineData("")]
    [InlineData(null)]
    // A braced GUID is a real directory name (driver store, COM component
    // folders) and must not be mistaken for a token: its hyphens exclude it.
    [InlineData(@"C:\App\{3f2504e0-4f89-11d3-9a0c-0305e82c3301}\x")]
    // A name starting with a digit is not an identifier.
    [InlineData(@"C:\App\{1234}\x")]
    // Braces around something that is plainly prose, not a token.
    [InlineData(@"C:\App\{not a token}\x")]
    // Unterminated: nothing to name, so nothing to refuse.
    [InlineData(@"C:\App\{install_dir")]
    // Empty braces.
    [InlineData(@"C:\App\{}\x")]
    public void Anything_that_is_not_an_identifier_in_braces_is_left_alone(string? value)
        => BraceTokenScanner.FirstUnresolved(value).Should().BeNull();

    /// <summary>
    /// The cross-lane property: the scan runs over the ALREADY-RESOLVED string,
    /// so every token the engine knows has been substituted away before it is
    /// reached. That is what makes it safe against a token another lane adds —
    /// there is no allow-list to fall out of date — and the scanner never
    /// resolves anything itself (resolving <c>{staging_dir}</c> creates a
    /// directory and can throw, so a validator that resolved in order to validate
    /// would have side effects). When lane S3 lands, adding <c>"{staging_dir}"</c>
    /// to this list is the whole change required.
    /// </summary>
    [Theory]
    [InlineData("{install_dir}")]
    [InlineData("{scope_root}")]
    [InlineData("{app.name}")]
    [InlineData("{app.id}")]
    [InlineData("{temp_dir}")]
    public void No_token_the_context_knows_survives_resolution(string token)
    {
        var blob = Blob();
        var ctx = StepContext.From(blob, CommandLineParser.Parse(new[] { "/silent" }, blob.Parameters));

        var resolved = ctx.ResolvePath(token + "/payload.bin");

        resolved.Should().NotContain("{");
        BraceTokenScanner.FirstUnresolved(resolved).Should().BeNull();
    }

    // ── The resolver refuses ──────────────────────────────────────────────────

    [Fact]
    public void ResolvePath_throws_for_a_token_that_never_resolved()
    {
        var ctx = new StepContext(new Dictionary<string, object?>(), installDir: @"C:\App");

        var act = () => ctx.ResolvePath("{install_dir}/{var.dest}/x");

        act.Should().Throw<FormatException>().WithMessage("*unresolved token '{var.dest}'*");
    }

    [Fact]
    public void Resolve_is_unaffected_so_non_path_fields_keep_their_braces()
    {
        // Only PATH fields are constrained. A registry value or an ini value may
        // legitimately contain braces — a GUID, a JSON fragment, a format string.
        var ctx = new StepContext(new Dictionary<string, object?>(), installDir: @"C:\App");

        ctx.Resolve("{var.not_declared}").Should().Be("{var.not_declared}");
    }

    // ── Every path-valued field, not just the guarded destinations ────────────

    [Fact]
    public async Task Directory_create_refuses_rather_than_making_a_literal_token_directory()
    {
        // R16's headline symptom, in the step that had no guard at all.
        using var installDir = new TempDir();
        var literal = Path.Combine(installDir.Path, "{var.typo}");

        var result = await RunThroughEngineAsync(
            installDir.Path,
            new InstallStep.DirectoryCreate("mk", Path.Combine(installDir.Path, "{var.typo}"), null, OnFailure.Fail));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("unresolved token '{var.typo}'");
        result.Journal.Records.Should().BeEmpty();
        Directory.Exists(literal).Should().BeFalse(
            "a literal '{var.typo}' directory is exactly the regression this closes");
    }

    [Fact]
    public async Task Run_program_refuses_an_unresolved_token_in_its_program()
    {
        // run_program has no containment guard — the token check is at the
        // resolver, so it is covered anyway. Nothing is launched.
        var result = await RunThroughEngineAsync(
            @"C:\App",
            new InstallStep.RunProgram(
                "run", "{var.typo}/setup.exe", null, Wait: true, Cwd: null,
                ExpectedExitCodes: null, TimeoutSeconds: null, null, OnFailure.Fail));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("unresolved token '{var.typo}'");
        result.Journal.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task The_containment_opt_out_does_not_excuse_an_unresolved_token()
    {
        using var installDir = new TempDir();

        var result = await RunThroughEngineAsync(
            installDir.Path,
            new InstallStep.FileCopy("cp", Path.Combine(installDir.Path, "*"),
                Path.Combine(installDir.Path, "{var.dest}"), Overwrite: true, null, OnFailure.Fail)
            { AllowOutsideInstallDir = true });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("unresolved token");
        result.Journal.Records.Should().BeEmpty();
    }

    /// <summary>
    /// Drive the step through <see cref="InstallEngine"/> rather than calling its
    /// <c>RunAsync</c> directly. <see cref="StepContext.ResolvePath"/> signals by
    /// throwing <see cref="FormatException"/> — the contract it has had all along
    /// for a <c>payload://</c> traversal — and the engine is what converts that
    /// into the typed step failure a publisher actually sees. Asserting through
    /// the engine proves the end-user-visible behaviour rather than just the throw.
    /// </summary>
    private static Task<EngineResult> RunThroughEngineAsync(string installDir, InstallStep step) =>
        new InstallEngine().RunAsync(new[] { step }, Ctx(installDir), CancellationToken.None);

    private static StepContext Ctx(string installDir) =>
        new(new Dictionary<string, object?>(), installDir: installDir);

    private static WrapperBlob Blob() =>
        new(
            AppId: "com.acme.Studio",
            Parameters: Array.Empty<ParameterDefinition>(),
            InstallSteps: Array.Empty<InstallStep>(),
            PreInstall: Array.Empty<InstallStep>(),
            PostInstall: Array.Empty<InstallStep>(),
            UpdateSteps: Array.Empty<InstallStep>(),
            Scope: InstallScope.Auto,
            Options: null,
            AppName: "Acme Studio",
            InstallDir: null);
}

/// <summary>
/// The same property on the three path-taking steps whose types are
/// Windows-only, so they need their own <c>[SupportedOSPlatform]</c> class.
/// </summary>
/// <remarks>
/// None of these creates anything: each returns on the resolver's refusal, before
/// <c>schtasks.exe</c> / <c>sc.exe</c> is started and before any shortcut COM call.
/// Every one asserts the rollback journal is EMPTY, which is the load-bearing
/// assertion rather than a nicety — these tests run with <c>OnFailure.Fail</c>, so
/// the engine replays the journal on the way out. A journaled
/// <c>DeleteShortcut</c> would therefore be EXECUTED, deleting a same-named
/// pre-existing <c>.lnk</c> from the real Desktop or Start Menu of whatever
/// machine ran the suite.
/// </remarks>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class UnresolvedPathTokenWindowsTests
{
    [WindowsFact("Windows shell APIs")]
    public async Task Shortcut_create_refuses_an_unresolved_token_in_its_target()
    {
        // shortcut_create has no containment guard — the token check is at the
        // resolver, so it is covered anyway.
        var result = await RunThroughEngineAsync(
            new InstallStep.ShortcutCreate(
                "sc", "{var.typo}/app.exe", "desktop", "App",
                Args: null, WorkingDir: null, Icon: null, Description: null, null, OnFailure.Fail));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("unresolved token '{var.typo}'");
        result.Journal.Records.Should().BeEmpty(
            "the refusal happens before the journal entry — a DeleteShortcut queued here would " +
            "be replayed by the engine and would delete a real Desktop shortcut named 'App.lnk'");
    }

    [WindowsFact("Windows shell APIs")]
    public async Task Shortcut_create_refuses_an_unresolved_token_in_its_location()
    {
        // R16 verbatim, in the one path field that reached NO substitution at all,
        // so a check on substituted output could never have caught it:
        // `location` fed Directory.CreateDirectory raw. The literal directory is
        // asserted absent relative to the current directory, which is where an
        // unrooted path would have landed.
        var literal = Path.Combine(Directory.GetCurrentDirectory(), "{install_dir}");
        Directory.Exists(literal).Should().BeFalse("precondition: no leftover from an earlier run");

        var result = await RunThroughEngineAsync(
            new InstallStep.ShortcutCreate(
                "sc", @"C:\App\app.exe", @"{var.typo}\Tools", "App",
                Args: null, WorkingDir: null, Icon: null, Description: null, null, OnFailure.Fail));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("unresolved token '{var.typo}'");
        result.Journal.Records.Should().BeEmpty();
        Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), "{var.typo}"))
            .Should().BeFalse("the location directory must not have been created");
    }

    [WindowsFact("Windows shell APIs")]
    public async Task Shortcut_create_substitutes_its_location_rather_than_taking_it_literally()
    {
        // The shipped example manifest uses `location: "${parameters.install_dir}\StartMenu"`.
        // Before the fix nothing substituted `location`, so following the
        // documentation produced a directory named after the template text.
        using var installDir = new TempDir();
        var expected = Path.Combine(installDir.Path, "StartMenu");
        var literal = Path.Combine(Directory.GetCurrentDirectory(), "{install_dir}");

        try
        {
            var result = await new InstallEngine().RunAsync(
                new InstallStep[]
                {
                    new InstallStep.ShortcutCreate(
                        "sc", Path.Combine(installDir.Path, "app.exe"), @"{install_dir}\StartMenu", "App",
                        Args: null, WorkingDir: null, Icon: null, Description: null, null, OnFailure.Fail),
                },
                new StepContext(new Dictionary<string, object?>(), installDir: installDir.Path),
                CancellationToken.None);

            result.Success.Should().BeTrue(result.Error);
            Directory.Exists(expected).Should().BeTrue("{install_dir} must have expanded");
            File.Exists(Path.Combine(expected, "App.lnk")).Should().BeTrue();
            Directory.Exists(literal).Should().BeFalse(
                "a literal '{install_dir}' directory is exactly the regression this closes");

            // This is the ONE test in the lane that legitimately journals a
            // DESTRUCTIVE record (DeleteShortcut unlinks a .lnk unconditionally).
            // Pin that its target is the shortcut this test just created, inside a
            // temp directory — so even if the engine replayed the journal it could
            // not touch a real Desktop or Start Menu entry.
            result.Journal.Records.Should().ContainSingle()
                .Which.Should().BeOfType<RollbackRecord.DeleteShortcut>()
                .Which.Path.Should().Be(Path.Combine(expected, "App.lnk"));
        }
        finally
        {
            // Only reachable if the fix regressed; leaving it would poison the
            // next run rather than failing this one.
            if (Directory.Exists(literal))
            {
                Directory.Delete(literal, recursive: true);
            }
        }
    }

    [WindowsFact("Windows shell APIs")]
    public async Task Shortcut_create_refuses_an_unresolved_token_in_its_name()
    {
        // `name` is a display string, so it goes through Resolve rather than
        // ResolvePath — but it is concatenated into the .lnk filename, so the
        // COMPOSED path is checked. Found by enumerating fields rather than by
        // reasoning about which ones "are paths".
        using var installDir = new TempDir();

        var result = await new InstallEngine().RunAsync(
            new InstallStep[]
            {
                new InstallStep.ShortcutCreate(
                    "sc", Path.Combine(installDir.Path, "app.exe"), installDir.Path, "{var.typo}",
                    Args: null, WorkingDir: null, Icon: null, Description: null, null, OnFailure.Fail),
            },
            new StepContext(new Dictionary<string, object?>(), installDir: installDir.Path),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("unresolved token '{var.typo}'");
        result.Journal.Records.Should().BeEmpty();
        File.Exists(Path.Combine(installDir.Path, "{var.typo}.lnk")).Should().BeFalse();
    }

    [WindowsFact("Windows service APIs")]
    public async Task Service_install_binary_path_is_a_path_field_and_gets_the_check()
    {
        // binary_path used ctx.Resolve, so it had neither this check nor the
        // payload:// traversal guard that every other path field has had.
        var result = await RunThroughEngineAsync(
            new InstallStep.ServiceInstall(
                "svc", "SigilTestService_DoesNotPersist", "{var.typo}/svc.exe", "Sigil Test",
                Description: null, StartType: "demand", ServiceAccount: "LocalSystem",
                StartAfterInstall: false, null, OnFailure.Fail));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("unresolved token '{var.typo}'");
        result.Journal.Records.Should().BeEmpty("sc.exe create must never be reached");
    }

    [WindowsFact("Windows task scheduler")]
    public async Task Scheduled_task_program_gets_the_check_too()
    {
        var result = await RunThroughEngineAsync(
            new InstallStep.ScheduledTaskCreate(
                "t", "SigilTestTask_DoesNotPersist", "{var.typo}/app.exe", Arguments: null,
                Trigger: "logon", RunLevel: "limited", null, OnFailure.Fail));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("unresolved token '{var.typo}'");
        result.Journal.Records.Should().BeEmpty("schtasks.exe must never be reached");
    }

    private static Task<EngineResult> RunThroughEngineAsync(InstallStep step) =>
        new InstallEngine().RunAsync(
            new[] { step },
            new StepContext(new Dictionary<string, object?>(), installDir: @"C:\App"),
            CancellationToken.None);
}
