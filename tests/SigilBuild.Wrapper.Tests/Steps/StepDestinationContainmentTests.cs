namespace SigilBuild.Wrapper.Tests.Steps;

using System;
using System.Collections.Generic;
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
/// Register row R16: no step destination was contained. A config edit could name
/// an absolute path anywhere, walk out with <c>..</c>, or follow a directory
/// junction planted inside the install tree — and <c>File.WriteAllText</c>
/// truncates an existing target in place, so it keeps whatever access control
/// list the attacker's placeholder had. A brace token that never resolved was
/// left literal, so a typo created a directory named <c>{var.x}</c>.
/// </summary>
public sealed class StepDestinationContainmentTests
{
    private const string JunctionPrefix = "sigil-s24-junction-";

    // ── ini_write / json_edit / xml_edit (ConfigFileEditor) ───────────────────

    [Fact]
    public async Task Ini_write_refuses_an_absolute_path_outside_the_install_dir()
    {
        using var installDir = new TempDir();
        using var elsewhere = new TempDir();
        var target = Path.Combine(elsewhere.Path, "machine.ini");

        var result = await new IniWriteStep(
                new InstallStep.IniWrite("i", target, "app", "x", "9", CreateIfMissing: true, null, OnFailure.Fail))
            .RunAsync(Ctx(installDir.Path), new RollbackJournal(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("outside install_dir");
        File.Exists(target).Should().BeFalse("the refusal happens before anything is written");
    }

    [Fact]
    public async Task Json_edit_refuses_a_traversal_escape()
    {
        using var installDir = new TempDir();
        // The escape target is unique per run: it lands in the shared OS temp
        // directory, so a fixed name would let one run's leftovers make the next
        // run's "must not exist" assertion lie in either direction.
        var target = Path.Combine(installDir.Path, "..", $"sigil-escaped-{Guid.NewGuid():N}.json");
        var full = Path.GetFullPath(target);

        try
        {
            var result = await new JsonEditStep(
                    new InstallStep.JsonEdit("j", target, "/a", "2", CreateIfMissing: true, null, OnFailure.Fail))
                .RunAsync(Ctx(installDir.Path), new RollbackJournal(), CancellationToken.None);

            result.Success.Should().BeFalse();
            result.Error.Should().Contain("outside install_dir");
            File.Exists(full).Should().BeFalse();
        }
        finally
        {
            // Only reached if the guard regressed; leaving the file would then
            // poison the next run rather than failing this one.
            if (File.Exists(full))
            {
                File.Delete(full);
            }
        }
    }

    [WindowsFact("NTFS directory junctions")]
    public async Task Xml_edit_refuses_a_destination_reached_through_a_junction()
    {
        // The junction is INSIDE install_dir, so every textual prefix check says
        // "contained" while the write lands wherever the attacker pointed it.
        // Junctions need no privilege, which is what makes this the realistic
        // primitive rather than symlinks.
        using var installDir = new TempDir();
        using var elsewhere = new TempDir();
        Junction.SweepStale(installDir.Path, JunctionPrefix);
        var link = Path.Combine(installDir.Path, JunctionPrefix + Guid.NewGuid().ToString("N"));
        Junction.CreateOrFail(link, elsewhere.Path);

        try
        {
            var target = Path.Combine(link, "app.config");

            var result = await new XmlEditStep(
                    new InstallStep.XmlEdit("x", target, "/root/a", null, "new", CreateIfMissing: true, null, OnFailure.Fail))
                .RunAsync(Ctx(installDir.Path), new RollbackJournal(), CancellationToken.None);

            result.Success.Should().BeFalse();
            result.Error.Should().Contain("junction");
            File.Exists(Path.Combine(elsewhere.Path, "app.config"))
                .Should().BeFalse("the write must not have followed the junction");
        }
        finally
        {
            Junction.Remove(link);
        }
    }

    [Fact]
    public async Task Ini_write_accepts_a_destination_inside_the_install_dir()
    {
        using var installDir = new TempDir();
        var target = Path.Combine(installDir.Path, "conf", "app.ini");

        var result = await new IniWriteStep(
                new InstallStep.IniWrite("i", target, "app", "x", "9", CreateIfMissing: true, null, OnFailure.Fail))
            .RunAsync(Ctx(installDir.Path), new RollbackJournal(), CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);
        File.ReadAllText(target).Should().Contain("x=9");
    }

    [Fact]
    public async Task Ini_write_accepts_an_out_of_tree_destination_with_the_documented_opt_out()
    {
        // Some installers legitimately write outside the installed application —
        // a machine-wide config under %ProgramData% is the usual case.
        using var installDir = new TempDir();
        using var elsewhere = new TempDir();
        var target = Path.Combine(elsewhere.Path, "machine.ini");

        var result = await new IniWriteStep(
                new InstallStep.IniWrite("i", target, "app", "x", "9", CreateIfMissing: true, null, OnFailure.Fail)
                { AllowOutsideInstallDir = true })
            .RunAsync(Ctx(installDir.Path), new RollbackJournal(), CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);
        File.ReadAllText(target).Should().Contain("x=9");
    }

    // ── file_copy ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task File_copy_refuses_a_destination_outside_the_install_dir()
    {
        using var installDir = new TempDir();
        using var source = new TempDir();
        using var elsewhere = new TempDir();
        File.WriteAllText(Path.Combine(source.Path, "a.txt"), "A");
        var to = Path.Combine(elsewhere.Path, "landing");

        var result = await new FileCopyStep(
                new InstallStep.FileCopy("cp", Path.Combine(source.Path, "*"), to, Overwrite: true, null, OnFailure.Fail))
            .RunAsync(Ctx(installDir.Path), new RollbackJournal(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("outside install_dir");
        Directory.Exists(to).Should().BeFalse(
            "the guard runs before Directory.CreateDirectory, so not even the destination tree is made");
    }

    [Fact]
    public async Task File_copy_destination_now_goes_through_ResolvePath_so_payload_traversal_is_caught()
    {
        // file_copy called ctx.Resolve, not ctx.ResolvePath, so `to` bypassed even
        // the pre-existing payload:// traversal guard that every other path field
        // has had all along.
        using var payload = new TempDir();
        using var installDir = new TempDir();
        var ctx = new StepContext(
            new Dictionary<string, object?>(), payloadRoot: payload.Path, installDir: installDir.Path);

        var act = async () => await new FileCopyStep(
                new InstallStep.FileCopy("cp", "payload://a.txt", "payload://../escaped", Overwrite: true, null, OnFailure.Fail))
            .RunAsync(ctx, new RollbackJournal(), CancellationToken.None);

        await act.Should().ThrowAsync<FormatException>().WithMessage("*escapes the payload root*");
    }

    // ── file_delete / directory_delete ────────────────────────────────────────

    [Fact]
    public async Task File_delete_refuses_a_target_outside_the_install_dir()
    {
        using var installDir = new TempDir();
        using var elsewhere = new TempDir();
        var victim = Path.Combine(elsewhere.Path, "important.txt");
        File.WriteAllText(victim, "keep me");

        var result = await new FileDeleteStep(
                new InstallStep.FileDelete("del", victim, IfMissing: "fail", null, OnFailure.Fail))
            .RunAsync(Ctx(installDir.Path), new RollbackJournal(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("outside install_dir");
        File.Exists(victim).Should().BeTrue("the file must not have been deleted");
    }

    [Fact]
    public async Task Directory_delete_refuses_a_subtree_outside_the_install_dir()
    {
        using var installDir = new TempDir();
        using var elsewhere = new TempDir();
        File.WriteAllText(Path.Combine(elsewhere.Path, "important.txt"), "keep me");

        var result = await new DirectoryDeleteStep(
                new InstallStep.DirectoryDelete("dd", elsewhere.Path, Recursive: true, null, OnFailure.Fail))
            .RunAsync(Ctx(installDir.Path), new RollbackJournal(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("outside install_dir");
        Directory.Exists(elsewhere.Path).Should().BeTrue("the subtree must not have been deleted");
    }

    // ── http_download ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Http_download_refuses_a_dest_outside_the_install_dir_before_any_request()
    {
        using var installDir = new TempDir();
        using var elsewhere = new TempDir();
        var dest = Path.Combine(elsewhere.Path, "payload.bin");

        var result = await new HttpDownloadStep(
                new InstallStep.HttpDownload(
                    "dl", "https://example.invalid/f", dest, new string('a', 64),
                    TimeoutSeconds: 1, Retries: 0, null, OnFailure.Fail))
            .RunAsync(Ctx(installDir.Path), new RollbackJournal(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("outside install_dir");
        result.Error.Should().NotContain("https",
            "the containment refusal must come before the URL scheme check and before any request");
    }

    // ── Unresolved tokens ─────────────────────────────────────────────────────

    [Fact]
    public async Task An_unresolved_var_token_in_a_path_fails_the_step_instead_of_creating_a_literal_directory()
    {
        // The bug: an unknown {var.x} was left literal, so a single typo in an
        // installer.vars name silently created a folder called "{var.dest}" and
        // the install reported success.
        using var installDir = new TempDir();
        var literal = Path.Combine(installDir.Path, "{var.dest}");

        var result = await new FileCopyStep(
                new InstallStep.FileCopy("cp", Path.Combine(installDir.Path, "*"),
                    Path.Combine(installDir.Path, "{var.dest}"), Overwrite: true, null, OnFailure.Fail))
            .RunAsync(Ctx(installDir.Path), new RollbackJournal(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("unresolved token '{var.dest}'");
        Directory.Exists(literal).Should().BeFalse(
            "a literal '{var.dest}' directory is exactly the regression this closes");
    }

    [Fact]
    public async Task The_opt_out_does_not_excuse_an_unresolved_token()
    {
        using var installDir = new TempDir();

        var result = await new FileCopyStep(
                new InstallStep.FileCopy("cp", Path.Combine(installDir.Path, "*"),
                    Path.Combine(installDir.Path, "{var.dest}"), Overwrite: true, null, OnFailure.Fail)
                { AllowOutsideInstallDir = true })
            .RunAsync(Ctx(installDir.Path), new RollbackJournal(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("unresolved token");
    }

    [Fact]
    public async Task An_unresolved_install_dir_token_fails_rather_than_landing_a_literal_folder()
    {
        // StepContext.Empty leaves {install_dir} literal. In production the token
        // always resolves; if it ever did not, writing it verbatim would be the
        // T13 regression all over again.
        var result = await new IniWriteStep(
                new InstallStep.IniWrite("i", "{install_dir}/app.ini", "app", "x", "9", true, null, OnFailure.Fail))
            .RunAsync(StepContext.Empty, new RollbackJournal(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("unresolved token '{install_dir}'");
    }

    private static StepContext Ctx(string installDir) =>
        new(new Dictionary<string, object?>(), installDir: installDir);
}
