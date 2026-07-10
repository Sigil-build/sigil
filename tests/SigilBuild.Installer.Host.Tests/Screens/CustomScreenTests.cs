using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Installer.Host.Branding;
using SigilBuild.Installer.Host.ViewModels;
using SigilBuild.Installer.Host.Views.Screens;
using Xunit;

namespace SigilBuild.Installer.Host.Tests.Screens;

/// <summary>
/// T9 host coverage: the widget factory maps parameter types (and overrides) to
/// widgets, declared screens render + collect values into <c>param.*</c>, and the
/// rail reflects only the When-visible screens.
/// </summary>
public class CustomScreenTests
{
    private static ParameterDefinition Param(
        string name, ParameterType type, object? def = null, IReadOnlyList<string>? enums = null) =>
        new(name, type, def, enums, InstallTime: true, Description: name, Pattern: null, Min: null, Max: null);

    // ── Widget inference table ──────────────────────────────────────────────

    [Theory]
    [InlineData(ParameterType.Bool, null, WizardWidget.Checkbox)]
    [InlineData(ParameterType.Bool, "switch", WizardWidget.Switch)]
    [InlineData(ParameterType.Secret, null, WizardWidget.SecretInput)]
    [InlineData(ParameterType.Path, null, WizardWidget.PathInput)]
    [InlineData(ParameterType.String, null, WizardWidget.TextInput)]
    [InlineData(ParameterType.String, "textarea", WizardWidget.TextArea)]
    [InlineData(ParameterType.Int, null, WizardWidget.NumberInput)]
    [InlineData(ParameterType.Int, "slider", WizardWidget.Slider)]
    public void Widget_inference_from_type(ParameterType type, string? widget, WizardWidget expected)
    {
        WidgetFactory.Infer(type, widget, enumCount: 0).Should().Be(expected);
    }

    [Fact]
    public void Enum_infers_radio_for_small_sets_and_dropdown_for_large()
    {
        WidgetFactory.Infer(ParameterType.Enum, null, enumCount: 3).Should().Be(WizardWidget.Radio);
        WidgetFactory.Infer(ParameterType.Enum, null, enumCount: 5).Should().Be(WizardWidget.Dropdown);
        WidgetFactory.Infer(ParameterType.Enum, "dropdown", enumCount: 3).Should().Be(WizardWidget.Dropdown);
        WidgetFactory.Infer(ParameterType.Enum, "radio", enumCount: 9).Should().Be(WizardWidget.Radio);
    }

    // ── Reference Configure screen ──────────────────────────────────────────

    private static (IReadOnlyList<InstallerScreen> Screens, IReadOnlyList<ParameterDefinition> Parameters)
        BuildConfigure()
    {
        var parameters = new List<ParameterDefinition>
        {
            Param("server_address", ParameterType.String, "https://acme.internal"),
            Param("license_key", ParameterType.Secret),
            Param("channel", ParameterType.Enum, "stable", new[] { "stable", "beta", "nightly" }),
            Param("autostart", ParameterType.Bool, true),
        };
        var screens = new List<InstallerScreen>
        {
            new("configure", "Configure {app.name}", "Set preferences.", null, new List<ScreenField>
            {
                new("server_address", null),
                new("license_key", null),
                new("channel", "radio"),
                new("autostart", null),
            }),
        };
        return (screens, parameters);
    }

    private static InstallerViewModel NavigateToConfigure()
    {
        var (screens, parameters) = BuildConfigure();
        // No license loaded → License screen absent (T14). Decision-4 flow:
        // Welcome → Location (destination) → configure (custom).
        var vm = new InstallerViewModel(new BrandTokens { AppName = "Acme Studio" });
        vm.LoadScreens(screens, parameters);
        vm.Next(); // Welcome → Location
        vm.Next(); // Location → configure (custom)
        return vm;
    }

    [Fact]
    public void Configure_screen_reproduces_prototype_widgets_and_interpolated_title()
    {
        var vm = NavigateToConfigure();

        vm.CurrentStep.Should().Be(InstallerStep.Custom);
        var screen = vm.CurrentCustomScreen!;
        screen.Title.Should().Be("Configure Acme Studio");

        var widgets = screen.Fields.Select(f => f.Widget).ToList();
        widgets.Should().Equal(
            WizardWidget.TextInput,   // server_address
            WizardWidget.SecretInput, // license_key
            WizardWidget.Radio,       // channel (override)
            WizardWidget.Checkbox);   // autostart
    }

    [Fact]
    public void Collected_field_values_flow_into_param_map()
    {
        var vm = NavigateToConfigure();
        var screen = vm.CurrentCustomScreen!;

        // Toggle autostart off and set the license key.
        var autostart = screen.Fields.Single(f => f.ParamName == "autostart");
        autostart.BoolValue = false;
        var license = screen.Fields.Single(f => f.ParamName == "license_key");
        license.TextValue = "LK-1";

        var collected = vm.CollectedParameterValues;
        collected["autostart"].Should().Be("false");
        collected["license_key"].Should().Be("LK-1");
        collected["server_address"].Should().Be("https://acme.internal");
        collected["channel"].Should().Be("stable");
    }

    [Fact]
    public void Secret_field_default_masks_and_toggles_reveal()
    {
        var vm = NavigateToConfigure();
        var license = vm.CurrentCustomScreen!.Fields.Single(f => f.ParamName == "license_key");

        license.PasswordChar.Should().NotBe('\0', "a secret field masks by default");
        license.RevealSecret = true;
        license.PasswordChar.Should().Be('\0', "toggling reveal shows the value");
    }

    // ── Rail reflects only visible screens ──────────────────────────────────

    [Fact]
    public void Rail_omits_screens_whose_when_is_false()
    {
        var parameters = new List<ParameterDefinition>
        {
            Param("advanced", ParameterType.Bool, false),
        };
        var screens = new List<InstallerScreen>
        {
            new("configure", "Configure", null, null, new List<ScreenField> { new("advanced", null) }),
            new("advanced_opts", "Advanced", null, "param.advanced == true",
                new List<ScreenField> { new("advanced", null) }),
        };

        var vm = new InstallerViewModel(new BrandTokens { AppName = "Acme" });
        vm.LoadScreens(screens, parameters);

        var labels = vm.RailSteps.Select(r => r.Label).ToList();
        labels.Should().Contain("configure");
        labels.Should().NotContain("advanced_opts", "a screen whose when is false is absent from the rail");
        labels.Should().Contain("Install");
    }

    // ── CustomView renders one control per field ────────────────────────────

    [AvaloniaFact]
    public void CustomView_renders_one_row_per_field()
    {
        var vm = NavigateToConfigure();
        var view = new CustomView { DataContext = vm };

        var host = view.FindControl<StackPanel>("FieldsHost")!;
        host.Children.Count.Should().Be(vm.CurrentCustomScreen!.Fields.Count);
    }
}
