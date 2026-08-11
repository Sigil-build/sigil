namespace SigilBuild.Wrapper.Tests.Steps;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Steps;
using SigilBuild.Wrapper.Tests.Helpers;
using Xunit;

/// <summary>
/// Register rows R31 and R32 — the two places where a manifest-substitutable
/// value was concatenated into a syntax that gives it more authority than the
/// field it was written in.
/// </summary>
/// <remarks>
/// No test here creates a scheduled task, and none writes an INI file: every case
/// refuses inside <see cref="ScheduledTaskCreateStep.BuildCreateArgs"/>, a pure
/// function that starts no process, or inside <c>ini_write</c>'s transform, which
/// runs before the file is written. The two step-level cases additionally assert
/// that nothing was journaled and that the target file is byte-unchanged; the
/// pure-seam cases have no journal to assert on, which is precisely why they
/// cannot touch anything.
/// </remarks>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class StepValueInjectionTests
{
    // ── R31: schtasks /TR ─────────────────────────────────────────────────────

    [Fact]
    public void Task_program_containing_a_quote_is_refused()
    {
        // /TR's value is parsed by schtasks as its own mini command line, so an
        // embedded quote moves where the executable token ends.
        var act = () => ScheduledTaskCreateStep.BuildCreateArgs(
            name: "T", program: @"C:\a\b"" && calc.exe && """, arguments: null,
            trigger: "daily", runLevel: "limited");

        act.Should().Throw<ArgumentException>().WithMessage("*quote*");
    }

    [Fact]
    public void Task_program_with_a_single_leading_quote_is_refused_too()
    {
        var act = () => ScheduledTaskCreateStep.BuildCreateArgs(
            name: "T", program: @"""C:\a\b.exe", arguments: null,
            trigger: "logon", runLevel: "limited");

        act.Should().Throw<ArgumentException>().WithMessage("*quote*");
    }

    [Fact]
    public void An_ordinary_spaced_program_path_is_still_accepted()
    {
        // The quoting that makes a spaced path work is added by BuildCreateArgs
        // itself; refusing author-supplied quotes must not break that.
        var args = ScheduledTaskCreateStep.BuildCreateArgs(
            "T", @"C:\Program Files\Acme\updater.exe", arguments: null,
            trigger: "logon", runLevel: "limited");

        args[4].Should().Be("\"C:\\Program Files\\Acme\\updater.exe\"");
    }

    [Fact]
    public void Arguments_may_still_contain_quotes()
    {
        // Deliberately unrestricted: a quoted flag value is ordinary, and
        // `arguments` is appended after the executable token so it cannot
        // displace it.
        var args = ScheduledTaskCreateStep.BuildCreateArgs(
            "T", @"C:\App\app.exe", arguments: @"--path ""C:\Program Files\x""",
            trigger: "logon", runLevel: "limited");

        args[4].Should().Be(@"""C:\App\app.exe"" --path ""C:\Program Files\x""");
    }

    [Fact]
    public async Task The_step_reports_a_refused_program_as_a_step_failure_and_journals_nothing()
    {
        // Two properties, and the second is the one that bit us. The throw must
        // not escape RunAsync as an unhandled exception; and the validation must
        // run BEFORE the journal entry.
        //
        // This arrangement is the reachable one: a double quote survives
        // Path.GetFullPath, and IsAdminOnlyWritable inspects the CONTAINING
        // directory — so an otherwise perfectly contained, admin-only path with a
        // quote typo clears both privileged-target checks and lands here. With the
        // journal appended first, an `on_failure: continue` run would have queued a
        // DeleteScheduledTask for 'SigilTestTask_DoesNotPersist' — a name this
        // installer never created, quite possibly a PRE-EXISTING SYSTEM task — to
        // be executed on rollback or uninstall.
        var spec = new InstallStep.ScheduledTaskCreate(
            "t", "SigilTestTask_DoesNotPersist", @"C:\Windows\System32\a"" && calc.exe && """,
            Arguments: null, Trigger: "logon", RunLevel: "limited",
            When: null, OnFailure: OnFailure.Continue);
        var ctx = new StepContext(
            new System.Collections.Generic.Dictionary<string, object?>(),
            scope: InstallScope.Machine,
            installDir: Environment.SystemDirectory);
        var journal = new RollbackJournal();

        var result = await new ScheduledTaskCreateStep(spec).RunAsync(ctx, journal, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("quote");
        journal.Records.Should().BeEmpty(
            "the quote is rejected before the journal entry, so no delete is queued for a " +
            "same-named task this installer never created");
    }

    // ── R32: ini_write line injection ─────────────────────────────────────────

    [Fact]
    public void Ini_value_containing_a_newline_cannot_inject_a_section()
    {
        var act = () => IniEditor.Set("[app]\nx=1\n", "app", "x", "9\n[admin]\nenabled=true");

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("9\r[admin]\r")]
    [InlineData("9\r\n[admin]\r\nenabled=true")]
    public void A_carriage_return_in_a_value_is_refused_as_well_as_a_line_feed(string value)
    {
        var act = () => IniEditor.Set("[app]\r\nx=1\r\n", "app", "x", value);

        act.Should().Throw<ArgumentException>().WithMessage("*line feed*");
    }

    [Theory]
    [InlineData("[admin]", "x", "1")]
    [InlineData("app", "[admin]", "1")]
    [InlineData("app", "x", "[admin]")]
    public void A_leading_bracket_is_refused_in_section_key_and_value(string section, string key, string value)
    {
        var act = () => IniEditor.Set("[app]\r\nx=1\r\n", section, key, value);

        act.Should().Throw<ArgumentException>().WithMessage("*[*");
    }

    [Theory]
    [InlineData("app\ninjected", "x", "1")]
    [InlineData("app", "x\ninjected=1", "1")]
    public void A_newline_is_refused_in_the_section_and_the_key_too(string section, string key, string value)
    {
        var act = () => IniEditor.Set("[app]\r\nx=1\r\n", section, key, value);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void An_ordinary_edit_is_untouched()
    {
        IniEditor.Set("[app]\r\nx=1\r\n", "app", "x", "9")
            .Should().Be("[app]\r\nx=9\r\n");
    }

    [Fact]
    public async Task The_step_reports_a_refused_value_as_a_step_failure_and_leaves_the_file_alone()
    {
        using var installDir = new TempDir();
        var path = Path.Combine(installDir.Path, "a.ini");
        File.WriteAllText(path, "[app]\r\nx=1\r\n");

        var spec = new InstallStep.IniWrite(
            "i", path, "app", "x", "9\n[admin]\nenabled=true",
            CreateIfMissing: false, null, OnFailure.Fail);

        var journal = new RollbackJournal();
        var result = await new IniWriteStep(spec).RunAsync(
            new StepContext(new System.Collections.Generic.Dictionary<string, object?>(), installDir: installDir.Path),
            journal,
            CancellationToken.None);

        result.Success.Should().BeFalse();
        File.ReadAllText(path).Should().Be("[app]\r\nx=1\r\n", "the file must not have been rewritten");
        File.ReadAllText(path).Should().NotContain("[admin]");

        // The one refusal in this lane that legitimately DOES journal:
        // ConfigFileEditor snapshots the prior file before handing off to the
        // transform, and the transform is where R32 rejects. The record is a
        // RESTORE of a file inside this test's own temp directory —
        // non-destructive by type — and is pinned here so that "refused implies
        // empty journal" is never assumed in the one place it does not hold.
        journal.Records.Should().ContainSingle()
            .Which.Should().BeOfType<RollbackRecord.RestoreConfigFile>()
            .Which.OriginalPath.Should().Be(path);
    }
}
