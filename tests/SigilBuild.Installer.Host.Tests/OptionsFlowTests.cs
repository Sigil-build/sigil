using System.Linq;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Installer.Host.Branding;
using SigilBuild.Installer.Host.ViewModels;
using Xunit;

namespace SigilBuild.Installer.Host.Tests;

/// <summary>
/// T8 host coverage: the built-in Options screen (and its rail entry) appear IFF
/// the blob carries ≥ 1 enabled component; it renders one checkbox per component
/// (checked = resolved default); a <c>locked</c> component renders disabled; the
/// checkbox states flow into <see cref="InstallerViewModel.CollectedOptionValues"/>
/// for the engine. Decision 4 places it after the License screen.
/// </summary>
public sealed class OptionsFlowTests
{
    private static InstallerOptionComponent[] TwoOptions() => new[]
    {
        new InstallerOptionComponent("desktop_shortcut", Default: true, Locked: false),
        new InstallerOptionComponent("add_to_path", Default: true, Locked: false),
    };

    [Fact]
    public void Options_screen_and_rail_entry_present_with_a_checkbox_per_component()
    {
        var vm = new InstallerViewModel(new BrandTokens());
        vm.LoadOptions(TwoOptions());

        vm.RailSteps.Select(r => r.Label).Should().Contain("Options");
        vm.OptionItems.Should().HaveCount(2);
        vm.OptionItems.Select(o => o.Name).Should().BeEquivalentTo("desktop_shortcut", "add_to_path");
        vm.OptionItems.Should().OnlyContain(o => o.IsChecked, "each checkbox is seeded from its resolved default (true)");

        vm.Next(); // Welcome → InstallOptions (Location)
        vm.Next(); // InstallOptions → Options
        vm.CurrentStep.Should().Be(InstallerStep.Options);
    }

    [Fact]
    public void Options_screen_absent_when_no_component_is_enabled()
    {
        var vm = new InstallerViewModel(new BrandTokens());
        // No LoadOptions — mirrors an un-stamped host / a manifest with no options.

        vm.RailSteps.Select(r => r.Label).Should().NotContain("Options");

        vm.Next(); // Welcome → InstallOptions (Location)
        vm.CurrentStep.Should().Be(InstallerStep.InstallOptions);
        vm.Next(); // InstallOptions → Installing (no Options in between)
        vm.CurrentStep.Should().Be(InstallerStep.Installing);
    }

    [Fact]
    public void Locked_component_renders_disabled_and_stays_at_its_default()
    {
        var vm = new InstallerViewModel(new BrandTokens());
        vm.LoadOptions(new[]
        {
            new InstallerOptionComponent("add_to_path", Default: true, Locked: true),
        });

        var item = vm.OptionItems.Single();
        item.IsLocked.Should().BeTrue();
        item.IsEnabled.Should().BeFalse("a locked component's checkbox is disabled");
        item.IsChecked.Should().BeTrue();

        // A user cannot toggle a locked component off.
        item.IsChecked = false;
        item.IsChecked.Should().BeTrue("a locked component is always applied at its default");
    }

    [Fact]
    public void Collected_option_values_reflect_the_checkbox_states()
    {
        var vm = new InstallerViewModel(new BrandTokens());
        vm.LoadOptions(TwoOptions());

        vm.OptionItems.Single(o => o.Name == "desktop_shortcut").IsChecked = false;

        var collected = vm.CollectedOptionValues;
        collected["desktop_shortcut"].Should().BeFalse();
        collected["add_to_path"].Should().BeTrue();
    }

    // ── P10 (gap G11): app-defined custom components ─────────────────────────

    private static InstallerOptionComponent CustomComp(
        string name, LocalizedText label, bool @default = false, bool locked = false,
        LocalizedText? description = null, string? when = null) =>
        new(name, @default, locked, Custom: true, Label: label, Description: description, When: when);

    [Fact]
    public void Custom_component_renders_after_builtins_with_its_label()
    {
        var vm = new InstallerViewModel(new BrandTokens());
        vm.LoadOptions(new[]
        {
            new InstallerOptionComponent("desktop_shortcut", Default: true, Locked: false),
            CustomComp("sample_data", LocalizedText.Plain("Install sample data"), @default: true),
        });

        vm.OptionItems.Select(o => o.Name).Should().Equal("desktop_shortcut", "sample_data");
        var custom = vm.OptionItems.Single(o => o.Name == "sample_data");
        custom.Label.Should().Be("Install sample data");
        custom.IsChecked.Should().BeTrue();
    }

    [Fact]
    public void Custom_component_label_resolves_to_the_session_language_preference()
    {
        var vm = new InstallerViewModel(new BrandTokens());
        var label = new LocalizedText(new System.Collections.Generic.Dictionary<string, string>
        {
            ["en"] = "Sample data",
            ["de"] = "Beispieldaten",
        });

        vm.LoadOptions(new[] { CustomComp("sample_data", label) }, new[] { "de", "en" });

        vm.OptionItems.Single().Label.Should().Be("Beispieldaten");
    }

    [Fact]
    public void Custom_component_exposes_its_description()
    {
        var vm = new InstallerViewModel(new BrandTokens());
        vm.LoadOptions(new[]
        {
            CustomComp("sample_data", LocalizedText.Plain("Sample data"),
                description: LocalizedText.Plain("Copies a starter project")),
        });

        var custom = vm.OptionItems.Single();
        custom.Description.Should().Be("Copies a starter project");
        custom.HasDescription.Should().BeTrue();
    }

    [Fact]
    public void Custom_component_with_a_false_when_is_hidden()
    {
        var vm = new InstallerViewModel(new BrandTokens());
        vm.LoadOptions(new[]
        {
            CustomComp("pro_stuff", LocalizedText.Plain("Pro extras"), when: "false"),
            CustomComp("always", LocalizedText.Plain("Always")),
        });

        vm.OptionItems.Select(o => o.Name).Should().Equal(new[] { "always" },
            "a component whose `when` is false is not applicable and its row is hidden");
    }

    [Fact]
    public void Custom_component_with_a_true_when_is_shown()
    {
        var vm = new InstallerViewModel(new BrandTokens());
        vm.LoadOptions(new[]
        {
            CustomComp("pro_stuff", LocalizedText.Plain("Pro extras"), when: "true"),
        });

        vm.OptionItems.Should().ContainSingle(o => o.Name == "pro_stuff");
    }

    [Fact]
    public void Options_sits_after_license_in_the_rail()
    {
        var vm = new InstallerViewModel(new BrandTokens());
        vm.LoadLicense("EULA text");
        vm.LoadOptions(TwoOptions());

        var labels = vm.RailSteps.Select(r => r.Label).ToList();
        labels.IndexOf("License").Should().BeLessThan(labels.IndexOf("Options"),
            "decision 4 orders welcome → destination → license → options");
        labels.IndexOf("Location").Should().BeLessThan(labels.IndexOf("License"));
    }
}
