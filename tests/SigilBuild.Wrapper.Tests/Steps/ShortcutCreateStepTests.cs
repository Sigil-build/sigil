namespace SigilBuild.Wrapper.Tests.Steps;

using System;
using System.IO;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Steps;
using SigilBuild.Wrapper.Steps.Win32;
using SigilBuild.Wrapper.Tests.Helpers;
using Xunit;

/// <summary>
/// Tests for the <c>shortcut_create</c> step. The full <c>IShellLinkW</c>
/// read-back round trip is intentionally omitted (see <c>ShellLink</c>'s
/// remarks on the Task 16 deferral) — we verify success by the .lnk file
/// existing, having content, and starting with the canonical Shell Link
/// Header magic <c>4C 00 00 00</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public class ShortcutCreateStepTests
{
    [Fact]
    public async Task Creates_lnk_file_with_target()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var sandbox = new TempDir();
        var location = Path.Combine(sandbox.Path, "StartMenu");
        Directory.CreateDirectory(location);

        var spec = new InstallStep.ShortcutCreate(
            Id: "s",
            Target: @"C:\Windows\notepad.exe",
            Location: location,
            Name: "Notepad",
            Args: new[] { @"C:\file.txt" },
            WorkingDir: @"C:\Windows",
            Icon: null,
            Description: "Open file",
            When: null,
            OnFailure: OnFailure.Fail);

        var journal = new RollbackJournal();
        var result = await new ShortcutCreateStep(spec)
            .RunAsync(StepContext.Empty, journal, default);

        result.Success.Should().BeTrue();
        var lnk = Path.Combine(location, "Notepad.lnk");
        File.Exists(lnk).Should().BeTrue();
        new FileInfo(lnk).Length.Should().BeGreaterThan(0);
        ShellLink.LooksLikeShellLink(lnk).Should().BeTrue(
            "the file must start with the Shell Link Header magic 4C 00 00 00");

        // Rollback deletes the .lnk
        await journal.UndoAsync(default);
        File.Exists(lnk).Should().BeFalse();
    }

    [Fact]
    public async Task ResolveLocation_handles_arbitrary_path()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Use an arbitrary path for the test — named locations (start_menu /
        // desktop) point at the calling user's profile dirs which we don't
        // want to pollute in tests.
        using var sandbox = new TempDir();
        var spec = new InstallStep.ShortcutCreate(
            Id: "s",
            Target: @"C:\Windows\System32\cmd.exe",
            Location: sandbox.Path,
            Name: "Foo",
            Args: null,
            WorkingDir: null,
            Icon: null,
            Description: null,
            When: null,
            OnFailure: OnFailure.Fail);

        var journal = new RollbackJournal();
        var result = await new ShortcutCreateStep(spec)
            .RunAsync(StepContext.Empty, journal, default);

        result.Success.Should().BeTrue();
        var lnkPath = Path.Combine(sandbox.Path, "Foo.lnk");
        File.Exists(lnkPath).Should().BeTrue();
        ShellLink.LooksLikeShellLink(lnkPath).Should().BeTrue();
    }

    [Fact]
    public async Task Rollback_deletes_lnk_and_is_safe_to_run_twice()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var sandbox = new TempDir();
        var spec = new InstallStep.ShortcutCreate(
            Id: "s",
            Target: @"C:\Windows\notepad.exe",
            Location: sandbox.Path,
            Name: "Twice",
            Args: null,
            WorkingDir: null,
            Icon: null,
            Description: null,
            When: null,
            OnFailure: OnFailure.Rollback);

        var journal = new RollbackJournal();
        await new ShortcutCreateStep(spec).RunAsync(StepContext.Empty, journal, default);
        var lnk = Path.Combine(sandbox.Path, "Twice.lnk");
        File.Exists(lnk).Should().BeTrue();

        await journal.UndoAsync(default);
        File.Exists(lnk).Should().BeFalse();

        // Second undo must not throw — DeleteShortcut is best-effort and
        // the journal swallows individual failures on top of that.
        await journal.UndoAsync(default);
    }

    [Fact]
    public async Task Creates_directory_when_location_does_not_exist()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var sandbox = new TempDir();
        // Sub-dir that does not exist yet — the step must mkdir -p it.
        var nested = Path.Combine(sandbox.Path, "Programs", "Sigil");
        Directory.Exists(nested).Should().BeFalse();

        var spec = new InstallStep.ShortcutCreate(
            Id: "s",
            Target: @"C:\Windows\notepad.exe",
            Location: nested,
            Name: "Sigil",
            Args: null,
            WorkingDir: null,
            Icon: null,
            Description: null,
            When: null,
            OnFailure: OnFailure.Fail);

        var journal = new RollbackJournal();
        var result = await new ShortcutCreateStep(spec)
            .RunAsync(StepContext.Empty, journal, default);

        result.Success.Should().BeTrue();
        Directory.Exists(nested).Should().BeTrue();
        File.Exists(Path.Combine(nested, "Sigil.lnk")).Should().BeTrue();
    }
}
