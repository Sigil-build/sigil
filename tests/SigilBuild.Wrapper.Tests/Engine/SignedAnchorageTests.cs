namespace SigilBuild.Wrapper.Tests.Engine;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Cli;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Tests.Helpers;
using Xunit;

/// <summary>
/// Register rows R44 and R51: the replay anchor resolves what it PERMITS from the signed
/// blob, and from nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <strong>R44</strong> — lane S2 shipped <c>allow_outside_install_dir</c> as a documented
/// manifest opt-out, with <c>%ProgramData%\MyApp</c> as the worked example, while lane
/// S1's anchor accepted only records pointing inside <c>install_dir</c>. Every such record
/// was refused at uninstall while the ARP row and the state were deleted anyway: an app
/// that cannot be removed, reached by following the documentation.
/// </para>
/// <para>
/// <strong>R51</strong> — registry anchoring was a denylist, and three consecutive review
/// rounds each produced another key shape it had missed. The tests below do not add a
/// fourth round of names. They prove the four that escaped are refused <em>without being
/// named</em>, and that key shapes nobody has enumerated at all — which the denylist
/// happily allowed — are refused by the same rule.
/// </para>
/// <para>
/// <strong>The invariant both rows exist to protect:</strong> nothing is trusted from the
/// journal. The rejected design for both was a per-record marker saying "I was declared",
/// which is a record asserting its own permission — something a planted journal asserts
/// just as easily. Every permission here comes from <see cref="SignedDeclarations"/>,
/// whose only inputs are the blob and literal test values;
/// <see cref="Anchoring_permission_never_comes_from_the_record_being_judged"/> pins that
/// behaviourally.
/// </para>
/// <para>
/// <strong>Nothing here can damage the host.</strong> Records naming real machine
/// coordinates are only ever handed to <c>ReplayAnchor.Check</c>, a pure predicate that
/// never invokes <c>UndoAsync</c>. The two tests that do execute a replay name only files
/// inside a <see cref="TempDir"/> this test created.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public class SignedAnchorageTests
{
    /// <summary>The only key the fixture manifest declares. Nothing else is allowed.</summary>
    private const string DeclaredKey = @"Software\Acme\App";

    // ---------------------------------------------------------------------------
    // R44 — a declared out-of-tree destination is anchored.
    // ---------------------------------------------------------------------------

    [WindowsFact("Windows path semantics")]
    public async Task A_record_under_a_declared_out_of_tree_destination_is_replayed()
    {
        // Arrange — the documented S2 shape: a step writes a machine-wide config outside
        // install_dir under `allow_outside_install_dir: true`, so its rollback record
        // points outside install_dir too. Before R44 this record was REFUSED while the
        // ARP row and the uninstall state were removed anyway. The stand-in for
        // %ProgramData%\MyApp is a scratch directory — the same shape (a per-app
        // directory inside a shared root), and safe to actually delete.
        using var installDir = new TempDir();
        using var outOfTree = new TempDir();
        var machineIni = Path.Combine(outOfTree.Path, "machine.ini");
        File.WriteAllText(machineIni, "endpoint=https://api.example.com");

        var journal = new RollbackJournal();
        journal.Append(new RollbackRecord.RestoreFile(machineIni, ExistedBefore: false, BackupPath: null));

        // Act — this one REPLAYS, to prove the allowance is wired through UndoAsync and
        // not merely computable by the predicate.
        var outcome = await journal.UndoAsync(
            Anchorage(installDir.Path, destinations: new[] { outOfTree.Path }),
            progress: null,
            ct: CancellationToken.None);

        // Assert
        outcome.RefusedRecords.Should().BeEmpty(
            "the manifest declared this destination with allow_outside_install_dir, so its " +
            "rollback record must replay — refusing it leaves the app unremovable while the " +
            "ARP row and the uninstall state are deleted anyway (R44)");
        File.Exists(machineIni).Should().BeFalse(
            "the record's undo must actually have run, not merely been permitted");
    }

    [WindowsFact("Windows path semantics")]
    public async Task A_record_outside_install_dir_that_nothing_declares_is_still_refused()
    {
        // Arrange — the same record, minus the declaration. This is what stops R44's fix
        // from degenerating into "records outside install_dir are fine now".
        using var installDir = new TempDir();
        using var outOfTree = new TempDir();
        var planted = Path.Combine(outOfTree.Path, "machine.ini");
        File.WriteAllText(planted, "prior");

        var journal = new RollbackJournal();
        journal.Append(new RollbackRecord.RestoreFile(planted, ExistedBefore: false, BackupPath: null));

        // Act
        var outcome = await journal.UndoAsync(
            Anchorage(installDir.Path), progress: null, ct: CancellationToken.None);

        // Assert
        var refusal = outcome.RefusedRecords.Should().ContainSingle().Subject;
        refusal.Code.Should().Be(ReplayRefusalCode.PathOutsideInstallRoots);
        File.Exists(planted).Should().BeTrue("a refused record must not have been replayed");
    }

    [WindowsFact("Windows path semantics")]
    public void The_documented_ProgramData_example_clears_the_floor()
    {
        // Arrange — predicate only; NOTHING under %ProgramData% is created, read or
        // deleted. This is the exact path shape docs/guides/install-steps.md tells
        // publishers to write, and the whole point of R44 is that it must be anchored.
        var declared = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SigilS7ExampleApp");
        using var installDir = new TempDir();

        var record = new RollbackRecord.RestoreFile(
            Path.Combine(declared, "machine.ini"), ExistedBefore: false, BackupPath: null);

        // Act
        var verdict = Anchor(installDir.Path, destinations: new[] { declared }).Check(record);

        // Assert
        verdict.RefusalMessage.Should().BeNull(
            "%ProgramData%\\<App> is the documented example for allow_outside_install_dir");
    }

    [WindowsTheory("Windows path semantics")]
    [InlineData(@"C:\", "volume root")]
    [InlineData(@"C:\ProgramData", "well-known system folder")]
    [InlineData(@"C:\Windows\System32", "well-known system folder")]
    [InlineData(@"C:\Windows\System32\drivers\etc", "inside the Windows directory")]
    [InlineData(@"C:\Program Files", "well-known system folder")]
    public void A_declaration_too_broad_to_anchor_with_is_dropped_not_honoured(
        string declared, string why)
    {
        // Arrange — the floor. A declaration is trustworthy (it is signed) but "the
        // publisher meant to write here" and "every record read off disk may write
        // anywhere under here" are different claims. Predicate only: no file under any
        // of these paths is touched.
        using var installDir = new TempDir();
        var record = new RollbackRecord.RestoreFile(
            Path.Combine(declared, "planted.dll"), ExistedBefore: false, BackupPath: null);

        var anchor = Anchor(installDir.Path, destinations: new[] { declared });

        // Act
        var verdict = anchor.Check(record);

        // Assert
        verdict.RefusalMessage.Should().NotBeNull(
            $"anchoring to '{declared}' ({why}) would hand a planted journal a write " +
            "primitive over everything beneath it");
        anchor.Notices.Should().NotBeEmpty(
            "a dropped declaration must be reported — silently ignoring one is how an " +
            "uninstall leaves files behind and says nothing");
    }

    [WindowsFact("Windows reparse points")]
    public void A_declared_root_reached_through_a_junction_is_dropped()
    {
        // Arrange — a junction needs no privilege on Windows, which makes it the
        // realistic way to defeat the rest of the floor: the declaration passes every
        // name-based check while pointing somewhere else entirely.
        using var parent = new TempDir();
        using var elsewhere = new TempDir();
        var junction = Path.Combine(parent.Path, "declared");
        CreateJunction(junction, elsewhere.Path);

        using var installDir = new TempDir();
        var record = new RollbackRecord.RestoreFile(
            Path.Combine(junction, "payload.dll"), ExistedBefore: false, BackupPath: null);

        // Act
        var anchor = Anchor(installDir.Path, destinations: new[] { junction });
        var verdict = anchor.Check(record);

        // Assert
        verdict.RefusalMessage.Should().NotBeNull(
            "a declared root reached through a junction does not provably name the location " +
            "the manifest declared");
        anchor.Notices.Should().Contain(n => n.Contains("junction", StringComparison.Ordinal));
    }

    [WindowsFact("Windows path + registry semantics")]
    public void A_declared_root_widens_file_containment_and_nothing_else()
    {
        // Arrange — the line that must not move. R44 widens where FILE records may point.
        // It must not touch the two predicates that decide whether the system will later
        // RUN something: a machine PATH entry and a machine-wide execution mapping both
        // require a target inside install_dir that only administrators can write, and a
        // manifest declaration is not a way around that.
        using var installDir = new TempDir();
        using var declared = new TempDir();
        var anchor = Anchor(installDir.Path, destinations: new[] { declared.Path });

        var pathRecord = new RollbackRecord.RestoreEnv(
            "machine", "Path", declared.Path, PreviouslyAbsent: false);
        var executionMapping = new RollbackRecord.RestoreRegistryValue(
            "HKLM",
            $@"{DeclaredKey}\shell\open\command",
            Name: "",
            View: "default",
            PriorTypeStr: "REG_SZ",
            PriorValue: $"\"{Path.Combine(declared.Path, "acme.exe")}\" \"%1\"",
            PreviouslyAbsent: false);

        // Act
        var envVerdict = anchor.Check(pathRecord);
        var mappingVerdict = Anchor(installDir.Path, destinations: new[] { declared.Path },
            keys: new[] { DeclaredKey }).Check(executionMapping);

        // Assert
        envVerdict.RefusalMessage.Should().NotBeNull(
            "a declared out-of-tree destination must never become an acceptable machine PATH entry");
        mappingVerdict.Refusal.Should().NotBeNull();
        mappingVerdict.Refusal!.Code.Should().Be(ReplayRefusalCode.ExecutionMappingNotOwned,
            "the key was declared, so the declaration check passed — and the execution-mapping " +
            "rule then refused it anyway, which is the layering R44 must not disturb");
    }

    // ---------------------------------------------------------------------------
    // R51 — the registry allowlist.
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Key shapes the denylist never named, and never would have: each is ordinary
    /// <c>Software\…</c> configuration space by every structural test S1 wrote, and each
    /// is a machine- or user-wide hijack. This is the class R51 says enumeration was
    /// losing to — not four more names to add.
    /// </summary>
    [WindowsTheory("Windows registry semantics")]
    [InlineData(@"Software\Classes\.txt", "repoints an extension at another progid")]
    [InlineData(@"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers", "per-binary compatibility shims")]
    [InlineData(@"Software\Microsoft\Windows\CurrentVersion\Authentication\Credential Provider Filters", "logon-time code")]
    [InlineData(@"Software\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers", "loads a handler into Explorer")]
    [InlineData(@"Software\Microsoft\Windows\CurrentVersion\WindowsUpdate", "machine update policy")]
    public void A_key_no_manifest_declared_is_refused_however_ordinary_it_looks(
        string key, string why)
    {
        // Arrange — predicate only; no registry key is opened, read or written. The
        // manifest declares one key and it is not this one.
        using var installDir = new TempDir();
        var record = new RollbackRecord.RestoreRegistryValue(
            "HKLM", key, "Value", "default", "REG_SZ", @"C:\Users\Public\evil.dll",
            PreviouslyAbsent: false);

        // Act
        var verdict = Anchor(installDir.Path, keys: new[] { DeclaredKey }).Check(record);

        // Assert
        verdict.Refusal.Should().NotBeNull(
            $"'{key}' ({why}) is inside Software\\ and has no dangerous SHAPE, so only a " +
            "manifest-declared allowlist can refuse it");
        verdict.Refusal!.Code.Should().Be(ReplayRefusalCode.RegistryKeyNotDeclared);
    }

    /// <summary>
    /// The four names three review rounds added to the denylist. They must be refused
    /// <em>because nothing declared them</em> — <c>DeclaredKey</c> is the entire
    /// allowlist and none of these appears in it. An allowlist that still needs them
    /// written down has not changed the shape of the problem.
    /// </summary>
    [WindowsTheory("Windows registry semantics")]
    [InlineData(@"Software\Classes\txtfile\shell\open\command")]
    [InlineData(@"Software\Classes\lnkfile\shell\open\command")]
    [InlineData(@"Software\Classes\mscfile\shell\open\command")]
    [InlineData(@"Software\Microsoft\Windows NT\CurrentVersion\Drivers32")]
    public void The_four_escaped_names_are_refused_without_being_named(string key)
    {
        // Arrange — predicate only.
        using var installDir = new TempDir();
        var record = new RollbackRecord.RestoreRegistryValue(
            "HKLM", key, "Value", "default", "REG_SZ", @"C:\Users\Public\evil.exe",
            PreviouslyAbsent: false);

        // Act
        var verdict = Anchor(installDir.Path, keys: new[] { DeclaredKey }).Check(record);

        // Assert
        verdict.Refusal.Should().NotBeNull();
        verdict.Refusal!.Code.Should().Be(
            ReplayRefusalCode.RegistryKeyNotDeclared,
            "the refusal must come from the allowlist, not from a name someone remembered " +
            "to add — the whole allowlist here is " + DeclaredKey);
        key.Should().NotContain(DeclaredKey, "the fixture must not accidentally declare the attack");
    }

    [WindowsTheory("Windows registry semantics")]
    [InlineData("HKCU", @"Software\Acme\App")]
    [InlineData("HKLM", @"Software\Acme\App\Settings")]
    [InlineData("HKLM", @"Software\WOW6432Node\Acme\App\Settings")]
    [InlineData("HKLM", @"Software/Acme/App/Settings")]
    public void A_declared_key_and_its_subtree_replay(string hive, string key)
    {
        // Arrange — the positives that keep real uninstalls working. Subtree, not exact
        // key: a recursive registry_delete_key reverses into records below the declared
        // key, and a manifest that declares Software\Acme\App writes under it.
        using var installDir = new TempDir();
        var record = new RollbackRecord.RestoreRegistryValue(
            hive, key, "Installed", "default", "REG_SZ", null, PreviouslyAbsent: true);

        // Act
        var verdict = Anchor(installDir.Path, keys: new[] { DeclaredKey }).Check(record);

        // Assert
        verdict.RefusalMessage.Should().BeNull(
            "the declared key must match across hive spellings, WOW6432Node views and " +
            "separator styles, or a healthy uninstall reports refusals");
    }

    [WindowsFact("Windows registry semantics")]
    public void A_declared_key_in_an_auto_run_surface_is_still_refused()
    {
        // Arrange — defence in depth, and the reason the denylist is KEPT rather than
        // deleted. The two layers answer different questions: "did this application
        // declare this key?" and "may any installer's rollback touch this at all?".
        // A manifest that declares an auto-run key — by mistake, or because a signing
        // key was misused — still cannot have one replayed out of a file on disk.
        using var installDir = new TempDir();
        const string autoRun = @"Software\Microsoft\Windows\CurrentVersion\Run";
        var record = new RollbackRecord.RestoreRegistryValue(
            "HKLM", autoRun, "Acme", "default", "REG_SZ", @"C:\Users\Public\evil.exe",
            PreviouslyAbsent: false);

        // Act
        var verdict = Anchor(installDir.Path, keys: new[] { autoRun }).Check(record);

        // Assert
        verdict.Refusal.Should().NotBeNull();
        verdict.Refusal!.Code.Should().Be(ReplayRefusalCode.RegistryOutsideApplicationSpace);
    }

    [WindowsFact("Windows registry semantics")]
    public void With_nothing_declared_every_registry_record_is_refused()
    {
        // Arrange — fail closed. An anchored replay with no signed artefact behind it
        // cannot tell an application's own key from anyone else's, so it permits none.
        using var installDir = new TempDir();
        var record = new RollbackRecord.RestoreRegistryValue(
            "HKCU", DeclaredKey, "Installed", "default", "REG_SZ", null, PreviouslyAbsent: true);

        // Act
        var verdict = Anchor(installDir.Path).Check(record);

        // Assert
        verdict.Refusal!.Code.Should().Be(ReplayRefusalCode.RegistryKeyNotDeclared);
    }

    // ---------------------------------------------------------------------------
    // The invariant: nothing is trusted from the journal.
    // ---------------------------------------------------------------------------

    [WindowsFact("Windows path + registry semantics")]
    public void Anchoring_permission_never_comes_from_the_record_being_judged()
    {
        // Arrange — the behavioural form of "the journal asserts nothing about its own
        // permissions". Two anchors, identical except for their DECLARATIONS; the
        // records are byte-identical. If any part of a record could influence what is
        // permitted, these two would agree.
        using var installDir = new TempDir();
        using var outOfTree = new TempDir();

        var fileRecord = new RollbackRecord.RestoreFile(
            Path.Combine(outOfTree.Path, "data.bin"), ExistedBefore: false, BackupPath: null);
        var registryRecord = new RollbackRecord.RestoreRegistryValue(
            "HKCU", DeclaredKey, "Installed", "default", "REG_SZ", null, PreviouslyAbsent: true);

        var withDeclarations = Anchor(
            installDir.Path, destinations: new[] { outOfTree.Path }, keys: new[] { DeclaredKey });
        var withNone = Anchor(installDir.Path);

        // Act / Assert
        withDeclarations.Check(fileRecord).RefusalMessage.Should().BeNull();
        withDeclarations.Check(registryRecord).RefusalMessage.Should().BeNull();

        withNone.Check(fileRecord).RefusalMessage.Should().NotBeNull(
            "the record is identical; only the blob's declarations differ, and they are the " +
            "only thing that may widen the anchor");
        withNone.Check(registryRecord).RefusalMessage.Should().NotBeNull();
    }

    [Fact]
    public void The_uninstall_entry_point_cannot_be_called_without_declarations()
    {
        // The declarations are an anchoring input, so they are a required parameter for
        // exactly the reason fallbackInstallDir is: an optional one is an invariant the
        // primitive cannot enforce and every call site has to remember.
        var act = async () => await new UninstallEngine()
            .RunAsync("sigil.x", @"C:\Program Files\Acme", declarations: null!);

        act.Should().ThrowAsync<ArgumentNullException>();
    }

    [WindowsFact("Windows path semantics")]
    public void Declarations_are_read_out_of_the_blob_and_resolved_against_the_recorded_dir()
    {
        // Arrange — the production path end to end: a blob whose steps declare an
        // out-of-tree destination and a registry key, resolved against the install
        // directory the replay chose. `{install_dir}` must expand to THAT directory —
        // the one recorded at install time — not to a recomputed default.
        using var recordedInstallDir = new TempDir();
        using var outOfTree = new TempDir();
        var declaredFile = Path.Combine(outOfTree.Path, "machine.ini");

        var blob = BlobDeclaring(
            new InstallStep.IniWrite(
                "cfg", declaredFile, "service", "endpoint", "https://api.example.com",
                CreateIfMissing: true, When: null, OnFailure: OnFailure.Fail)
            {
                AllowOutsideInstallDir = true,
            },
            new InstallStep.RegistryWrite(
                "reg", "HKCU", DeclaredKey, "Installed", "REG_SZ", "1", "default",
                When: null, OnFailure: OnFailure.Fail),
            new InstallStep.FileCopy(
                "app", "payload://app.exe", "{install_dir}/app.exe", Overwrite: true,
                When: null, OnFailure: OnFailure.Fail));

        var anchorage = ReplayAnchorage.ForInstall(
            recordedInstallDir.Path,
            "sigil.acme",
            InstallScope.User,
            SignedDeclarations.FromBlob(blob, CommandLineParser.Parse(Array.Empty<string>(), Array.Empty<ParameterDefinition>()), InstallScope.User));
        var anchor = ReplayAnchor.For(anchorage)!;

        // Act / Assert — the declared coordinates replay …
        anchor.Check(new RollbackRecord.RestoreFile(declaredFile, false, null))
            .RefusalMessage.Should().BeNull();
        anchor.Check(new RollbackRecord.RestoreRegistryValue(
                "HKCU", DeclaredKey, "Installed", "default", "REG_SZ", null, true))
            .RefusalMessage.Should().BeNull();

        // … a file_copy WITHOUT the opt-out declares nothing outside install_dir …
        anchor.Check(new RollbackRecord.RestoreFile(
                Path.Combine(Path.GetTempPath(), "sigil-s7-undeclared", "app.exe"), false, null))
            .RefusalMessage.Should().NotBeNull();

        // … and a record inside the recorded install dir is unaffected throughout.
        anchor.Check(new RollbackRecord.RestoreFile(
                Path.Combine(recordedInstallDir.Path, "app.exe"), false, null))
            .RefusalMessage.Should().BeNull();
    }

    // ---------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------

    private static ReplayAnchorage Anchorage(
        string installDir,
        IEnumerable<string>? destinations = null,
        IEnumerable<string>? keys = null) =>
        ReplayAnchorage.ForInstall(
            installDir,
            "sigil.acme",
            InstallScope.User,
            SignedDeclarations.ForLiterals(
                destinations,
                keys?.Select(k => new DeclaredRegistryKey("HKLM", k))));

    private static ReplayAnchor Anchor(
        string installDir,
        IEnumerable<string>? destinations = null,
        IEnumerable<string>? keys = null) =>
        ReplayAnchor.For(Anchorage(installDir, destinations, keys))!;

    private static WrapperBlob BlobDeclaring(params InstallStep[] steps) => new(
        AppId: "sigil.acme",
        Parameters: Array.Empty<ParameterDefinition>(),
        InstallSteps: steps,
        PreInstall: Array.Empty<InstallStep>(),
        PostInstall: Array.Empty<InstallStep>(),
        UpdateSteps: Array.Empty<InstallStep>());

    /// <summary>
    /// Create a directory junction. Junctions need no privilege on Windows — which is
    /// precisely why the floor has to account for them — but .NET exposes only symbolic
    /// links, which do, so this shells out the way an attacker would.
    /// </summary>
    private static void CreateJunction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var process = Process.Start(psi)!;
        process.WaitForExit(30_000);
        process.ExitCode.Should().Be(0, "creating a junction requires no privilege on Windows");
        new DirectoryInfo(link).LinkTarget.Should().NotBeNull("the fixture must really be a junction");
    }
}
