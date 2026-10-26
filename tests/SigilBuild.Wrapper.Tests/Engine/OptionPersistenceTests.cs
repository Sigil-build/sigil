// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Cli;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Tests.Helpers;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Engine;

/// <summary>
/// An install records the option values it resolved with its uninstall state, and
/// an upgrade or reinstall of the same app starts from them: below a wizard
/// checkbox and a <c>/P</c> override, above the manifest default. A locked
/// component keeps its manifest default, a recorded name the manifest no longer
/// declares is ignored, and a component new in this version takes its default.
/// (R98)
/// </summary>
public sealed class OptionPersistenceTests
{
    private static InstallerOptionComponent BuiltIn(string name, bool @default, bool locked = false) =>
        new(name, @default, locked);

    private static InstallerOptionComponent Custom(string name, bool @default = false, string? when = null) =>
        new(name, @default, Locked: false, Custom: true, Label: LocalizedText.Plain(name), When: when);

    private static WrapperBlob Blob(string appId, string version, params InstallerOptionComponent[] options) => new(
        AppId: appId,
        Parameters: Array.Empty<ParameterDefinition>(),
        InstallSteps: Array.Empty<InstallStep>(),
        PreInstall: Array.Empty<InstallStep>(),
        PostInstall: Array.Empty<InstallStep>(),
        UpdateSteps: Array.Empty<InstallStep>(),
        Scope: InstallScope.User,
        DisplayName: "Acme Studio",
        Publisher: "Acme, Inc.",
        Version: version,
        EstimatedSizeBytes: 4096,
        Options: options);

    private static ParsedCommandLine Parse(WrapperBlob blob, params string[] args)
    {
        var custom = new List<string>();
        foreach (var opt in blob.Options ?? Array.Empty<InstallerOptionComponent>())
        {
            if (opt.Custom)
            {
                custom.Add(opt.Name);
            }
        }
        return CommandLineParser.Parse(args, blob.Parameters, custom.Count == 0 ? null : custom);
    }

    private static string NewAppId() => "sigil.r98." + Guid.NewGuid().ToString("N");

    // ── The state store ─────────────────────────────────────────────────────

    [WindowsFact]
    public void State_store_round_trips_the_recorded_options()
    {
        // Arrange
        var appId = NewAppId();
        var recorded = new Dictionary<string, bool> { ["add_to_path"] = true, ["sample_data"] = false };

        try
        {
            // Act
            UninstallStateStore.Save(appId, new RollbackJournal(), InstallScope.User, options: recorded);
            var loaded = UninstallStateStore.TryLoad(appId, InstallScope.User);

            // Assert
            loaded.Should().NotBeNull();
            loaded!.Options.Should().BeEquivalentTo(recorded);
        }
        finally
        {
            UninstallStateStore.Delete(appId, InstallScope.User);
        }
    }

    [WindowsFact]
    public void State_written_before_the_field_existed_loads_with_no_options()
    {
        // Arrange — the exact shape an older Sigil wrote: no "options" property.
        var appId = NewAppId();
        Directory.CreateDirectory(UninstallStateStore.DirectoryFor(appId, InstallScope.User));
        File.WriteAllText(
            UninstallStateStore.PathFor(appId, InstallScope.User),
            $$"""{"appId":"{{appId}}","version":"1","records":[]}""");

        try
        {
            // Act
            var attempt = UninstallStateStore.Load(appId, InstallScope.User);

            // Assert
            attempt.RefusalReason.Should().BeNull("an older state file is not malformed");
            attempt.State.Should().NotBeNull();
            attempt.State!.Options.Should().BeNull();
        }
        finally
        {
            UninstallStateStore.Delete(appId, InstallScope.User);
        }
    }

    [WindowsFact]
    public void Saving_no_options_writes_no_options_field()
    {
        // Arrange
        var appId = NewAppId();

        try
        {
            // Act
            UninstallStateStore.Save(appId, new RollbackJournal(), InstallScope.User);
            var json = File.ReadAllText(UninstallStateStore.PathFor(appId, InstallScope.User));

            // Assert
            json.Should().NotContain("\"options\"");
            UninstallStateStore.TryLoad(appId, InstallScope.User)!.Options.Should().BeNull();
        }
        finally
        {
            UninstallStateStore.Delete(appId, InstallScope.User);
        }
    }

    // ── Resolution precedence ───────────────────────────────────────────────

    [Fact]
    public void Recorded_value_replaces_the_manifest_default()
    {
        // Arrange
        var blob = Blob("com.acme.Studio", "2.0.0", BuiltIn("add_to_path", @default: false), Custom("sample_data"));
        var prior = new Dictionary<string, bool> { ["add_to_path"] = true, ["sample_data"] = true };

        // Act
        var ctx = StepContext.From(blob, Parse(blob), priorOptions: prior);

        // Assert
        ctx.Evaluate("option.add_to_path").Should().BeTrue();
        ctx.Evaluate("option.sample_data").Should().BeTrue();
    }

    [Fact]
    public void Cli_override_beats_the_recorded_value()
    {
        // Arrange
        var blob = Blob("com.acme.Studio", "2.0.0", BuiltIn("add_to_path", @default: false), Custom("sample_data"));
        var prior = new Dictionary<string, bool> { ["add_to_path"] = true, ["sample_data"] = true };

        // Act
        var ctx = StepContext.From(
            blob, Parse(blob, "/Padd_to_path=false", "/Poption.sample_data=false"), priorOptions: prior);

        // Assert
        ctx.Evaluate("option.add_to_path == false").Should().BeTrue("/P still wins on an upgrade");
        ctx.Evaluate("option.sample_data == false").Should().BeTrue("/Poption. still wins on an upgrade");
    }

    [Fact]
    public void Wizard_checkbox_beats_the_recorded_value()
    {
        // Arrange
        var blob = Blob("com.acme.Studio", "2.0.0", BuiltIn("add_to_path", @default: false));
        var prior = new Dictionary<string, bool> { ["add_to_path"] = true };
        var collected = new Dictionary<string, bool> { ["add_to_path"] = false };

        // Act
        var ctx = StepContext.From(blob, Parse(blob), collectedOptions: collected, priorOptions: prior);

        // Assert
        ctx.Evaluate("option.add_to_path == false").Should().BeTrue();
    }

    [Fact]
    public void Locked_component_keeps_its_manifest_default_over_the_recorded_value()
    {
        // Arrange
        var blob = Blob("com.acme.Studio", "2.0.0", BuiltIn("start_menu", @default: true, locked: true));
        var prior = new Dictionary<string, bool> { ["start_menu"] = false };

        // Act
        var ctx = StepContext.From(blob, Parse(blob), priorOptions: prior);

        // Assert
        ctx.Evaluate("option.start_menu").Should().BeTrue();
        ctx.ResolvedOptions["start_menu"].Should().BeTrue();
    }

    [Fact]
    public void Removed_component_is_ignored_and_new_component_takes_its_default()
    {
        // Arrange — 1.0 had legacy_tools; 2.0 dropped it and added sample_data.
        var blob = Blob("com.acme.Studio", "2.0.0", Custom("sample_data", @default: true));
        var prior = new Dictionary<string, bool> { ["legacy_tools"] = true };

        // Act
        var ctx = StepContext.From(blob, Parse(blob), priorOptions: prior);

        // Assert
        ctx.Evaluate("option.sample_data").Should().BeTrue("a component new in this version starts at its default");
        ctx.ResolvedOptions.Should().ContainKey("sample_data").And.NotContainKey("legacy_tools");
    }

    [Fact]
    public void Resolved_options_leave_out_a_custom_component_its_when_made_inapplicable()
    {
        // Arrange
        var blob = Blob(
            "com.acme.Studio", "2.0.0",
            BuiltIn("desktop_shortcut", @default: true),
            Custom("pro_stuff", @default: true, when: "scope == 'machine'"));

        // Act
        var ctx = StepContext.From(blob, Parse(blob), scope: InstallScope.User);

        // Assert
        ctx.Evaluate("option.pro_stuff == false").Should().BeTrue();
        ctx.ResolvedOptions.Should().ContainKey("desktop_shortcut")
            .And.NotContainKey("pro_stuff", "an off that nobody chose must not be recorded as a choice");
    }

    // ── The session ─────────────────────────────────────────────────────────

    [WindowsFact]
    public void Upgrade_option_defaults_come_from_the_prior_install()
    {
        // Arrange — 1.0 recorded its choices; 2.0 is the upgrade.
        var appId = NewAppId();
        UninstallStateStore.Save(
            appId, new RollbackJournal(), InstallScope.User,
            options: new Dictionary<string, bool>
            {
                ["add_to_path"] = true,
                ["start_menu"] = false,
                ["sample_data"] = true,
                ["legacy_tools"] = true,
            });
        var blob = Blob(
            appId, "2.0.0",
            BuiltIn("add_to_path", @default: false),
            BuiltIn("start_menu", @default: true, locked: true),
            Custom("sample_data"),
            Custom("new_feature", @default: true));
        var installed = new UpgradeState(
            Found: true, InstalledVersion: "1.0.0",
            PriorInstallDir: Path.Combine(ScopeLayout.For(InstallScope.User).InstallRoot, appId),
            PriorUninstallExe: string.Empty, FoundScope: InstallScope.User);

        try
        {
            // Act
            var session = InstallSession.ForTesting(blob, Parse(blob), installed);

            // Assert
            session.UpgradeAction.Should().Be(UpgradeAction.Upgrade);
            session.OptionDefaults.Should().BeEquivalentTo(new Dictionary<string, bool>
            {
                ["add_to_path"] = true,   // recorded
                ["start_menu"] = true,    // locked: manifest default
                ["sample_data"] = true,   // recorded
                ["new_feature"] = true,   // new: manifest default
            });
        }
        finally
        {
            UninstallStateStore.Delete(appId, InstallScope.User);
        }
    }

    [WindowsFact]
    public void Upgrade_option_defaults_honour_a_cli_override()
    {
        // Arrange
        var appId = NewAppId();
        UninstallStateStore.Save(
            appId, new RollbackJournal(), InstallScope.User,
            options: new Dictionary<string, bool> { ["add_to_path"] = true, ["sample_data"] = true });
        var blob = Blob(appId, "2.0.0", BuiltIn("add_to_path", @default: false), Custom("sample_data"));

        try
        {
            // Act
            var session = InstallSession.ForTesting(
                blob, Parse(blob, "/Padd_to_path=false", "/Poption.sample_data=false"));

            // Assert
            session.OptionDefaults["add_to_path"].Should().BeFalse();
            session.OptionDefaults["sample_data"].Should().BeFalse();
        }
        finally
        {
            UninstallStateStore.Delete(appId, InstallScope.User);
        }
    }

    [WindowsFact]
    public void Recorded_options_survive_the_prior_state_being_deleted_once_read()
    {
        // Arrange — the prior version's teardown deletes the state directory before
        // the new install writes its own; the session must already hold the values.
        var appId = NewAppId();
        UninstallStateStore.Save(
            appId, new RollbackJournal(), InstallScope.User,
            options: new Dictionary<string, bool> { ["add_to_path"] = true });
        var blob = Blob(appId, "2.0.0", BuiltIn("add_to_path", @default: false));
        var session = InstallSession.ForTesting(blob, Parse(blob));
        _ = session.OptionDefaults;

        // Act
        UninstallStateStore.Delete(appId, InstallScope.User);

        // Assert
        session.OptionDefaults["add_to_path"].Should().BeTrue();
        session.ExistingInstallDetected.Should().BeTrue();
    }

    [WindowsFact]
    public void Fresh_install_option_defaults_are_the_manifest_defaults()
    {
        // Arrange
        var appId = NewAppId();
        var blob = Blob(appId, "1.0.0", BuiltIn("add_to_path", @default: false), Custom("sample_data", @default: true));

        // Act
        var session = InstallSession.ForTesting(blob, Parse(blob));

        // Assert
        session.ExistingInstallDetected.Should().BeFalse();
        session.OptionDefaults.Should().BeEquivalentTo(
            new Dictionary<string, bool> { ["add_to_path"] = false, ["sample_data"] = true });
    }

    [WindowsFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Reinstall_records_and_reapplies_the_options_chosen_at_first_install()
    {
        // Arrange
        var appId = NewAppId();
        var installDir = Path.Combine(ScopeLayout.For(InstallScope.User).InstallRoot, appId);
        var blob = Blob(appId, "1.0.0", BuiltIn("add_to_path", @default: false), Custom("sample_data"));

        try
        {
            // Act — the first install ticks both; the second passes nothing. The
            // second run's reinstall cleanup deletes the first run's state before it
            // persists its own, so what it records proves it read the first one first.
            var first = InstallSession.ForTesting(
                blob, Parse(blob, "/silent", "/Padd_to_path=true", "/Poption.sample_data=true"));
            (await first.RunInstallCoreAsync(Array.Empty<byte>(), progress: null)).Success.Should().BeTrue();
            var afterFirst = UninstallStateStore.TryLoad(appId, InstallScope.User)!.Options;

            // Nothing reads the second session's prior state before its run does.
            var second = InstallSession.ForTesting(blob, Parse(blob, "/silent"));
            (await second.RunInstallCoreAsync(Array.Empty<byte>(), progress: null)).Success.Should().BeTrue();
            var afterSecond = UninstallStateStore.TryLoad(appId, InstallScope.User)!.Options;

            // Assert
            var expected = new Dictionary<string, bool> { ["add_to_path"] = true, ["sample_data"] = true };
            afterFirst.Should().BeEquivalentTo(expected);
            afterSecond.Should().BeEquivalentTo(expected, "the reinstall resolved from the recorded choices");
        }
        finally
        {
#pragma warning disable CA1031 // Best-effort test cleanup.
            try { ArpRegistration.Remove(appId, InstallScope.User); } catch { }
            try { UninstallStateStore.Delete(appId, InstallScope.User); } catch { }
            try { if (Directory.Exists(installDir)) Directory.Delete(installDir, recursive: true); } catch { }
#pragma warning restore CA1031
        }
    }
}
