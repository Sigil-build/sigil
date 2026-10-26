// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Installer.Host.Branding;
using SigilBuild.Installer.Host.ViewModels;
using Xunit;

namespace SigilBuild.Installer.Host.Tests;

/// <summary>
/// The Options screen opens on the session's resolved defaults — on an upgrade,
/// the choices the prior install recorded — rather than the manifest defaults. A
/// locked component always shows its manifest default, and a component the
/// defaults do not name starts at its manifest default. (R98)
/// </summary>
public sealed class OptionDefaultsTests
{
    private static InstallerOptionComponent[] Components() => new[]
    {
        new InstallerOptionComponent("add_to_path", Default: false, Locked: false),
        new InstallerOptionComponent("start_menu", Default: true, Locked: true),
        new InstallerOptionComponent("sample_data", Default: false, Locked: false, Custom: true,
            Label: LocalizedText.Plain("Sample data")),
        new InstallerOptionComponent("new_feature", Default: true, Locked: false, Custom: true,
            Label: LocalizedText.Plain("New feature")),
    };

    [Fact]
    public void Checkboxes_start_at_the_supplied_defaults()
    {
        // Arrange
        var vm = new InstallerViewModel(new BrandTokens());
        var defaults = new Dictionary<string, bool>
        {
            ["add_to_path"] = true,
            ["start_menu"] = false,
            ["sample_data"] = true,
        };

        // Act
        vm.LoadOptions(Components(), defaults: defaults);

        // Assert
        var state = vm.OptionItems.ToDictionary(o => o.Name, o => o.IsChecked);
        state["add_to_path"].Should().BeTrue("the recorded choice replaces the manifest default");
        state["sample_data"].Should().BeTrue();
        state["start_menu"].Should().BeTrue("a locked component always shows its manifest default");
        state["new_feature"].Should().BeTrue("a component the defaults do not name starts at its manifest default");
    }

    [Fact]
    public void Seeded_values_are_what_the_install_collects()
    {
        // Arrange
        var vm = new InstallerViewModel(new BrandTokens());

        // Act
        vm.LoadOptions(Components(), defaults: new Dictionary<string, bool> { ["add_to_path"] = true });

        // Assert
        vm.CollectedOptionValues["add_to_path"].Should().BeTrue();
        vm.CollectedOptionValues["sample_data"].Should().BeFalse();
    }

    [Fact]
    public void Without_defaults_the_manifest_defaults_apply()
    {
        // Arrange
        var vm = new InstallerViewModel(new BrandTokens());

        // Act
        vm.LoadOptions(Components());

        // Assert
        vm.OptionItems.ToDictionary(o => o.Name, o => o.IsChecked).Should().BeEquivalentTo(
            new Dictionary<string, bool>
            {
                ["add_to_path"] = false,
                ["start_menu"] = true,
                ["sample_data"] = false,
                ["new_feature"] = true,
            });
    }

    [Fact]
    public void A_seeded_checkbox_can_still_be_changed()
    {
        // Arrange
        var vm = new InstallerViewModel(new BrandTokens());
        vm.LoadOptions(Components(), defaults: new Dictionary<string, bool> { ["add_to_path"] = true });
        var item = vm.OptionItems.Single(o => o.Name == "add_to_path");

        // Act
        item.IsChecked = false;

        // Assert
        vm.CollectedOptionValues["add_to_path"].Should().BeFalse();
    }
}
