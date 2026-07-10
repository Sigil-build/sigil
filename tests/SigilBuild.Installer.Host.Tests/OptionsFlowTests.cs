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
