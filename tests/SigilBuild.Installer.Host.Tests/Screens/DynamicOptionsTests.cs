using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Installer.Host.Branding;
using SigilBuild.Installer.Host.Services;
using SigilBuild.Installer.Host.ViewModels;
using Xunit;

namespace SigilBuild.Installer.Host.Tests.Screens;

/// <summary>
/// http-options runtime wiring: a source-backed enum field fetches its dropdown
/// options (via an injected, network-free fetcher), the selected value surfaces
/// into <c>param.*</c>, <c>${parameters.*}</c> URL placeholders are substituted
/// from collected values before the fetch, and a failed/empty fetch degrades
/// gracefully without crashing the wizard.
/// </summary>
public class DynamicOptionsTests
{
    private static ParameterDefinition Param(
        string name, ParameterType type, object? def = null,
        IReadOnlyList<string>? enums = null, ParameterSource? source = null,
        bool installTime = true) =>
        new(name, type, def, enums, installTime, Description: name,
            Pattern: null, Min: null, Max: null, Source: source);

    // ── Source forces a ComboBox, even with no static enum values ────────────

    [Fact]
    public void Source_backed_enum_infers_dropdown_not_radio()
    {
        var def = Param("app", ParameterType.Enum,
            source: new ParameterSource("https://api/apps", "data", "id", "name"));
        var field = new FieldViewModel(def, widgetOverride: null);

        field.Widget.Should().Be(WizardWidget.Dropdown);
        field.HasDynamicOptions.Should().BeTrue();
        field.DropdownOptions.Should().BeEmpty("a source-backed dropdown starts empty until the fetch runs");
    }

    // ── Injected canned options populate the dropdown; value flows to param.* ─

    [Fact]
    public async Task Field_populates_dropdown_from_injected_options_and_surfaces_selected_value()
    {
        var def = Param("app", ParameterType.Enum,
            source: new ParameterSource("https://api/apps", "data", "applicationId", "applicationName"));
        var field = new FieldViewModel(def, widgetOverride: null);

        var canned = new List<HttpOption>
        {
            new("Kiosk-01", "uuid-1"),
            new("Kiosk-02", "uuid-2"),
        };
        OptionsFetcher fetcher = (_, _, _, _, _) => Task.FromResult<IReadOnlyList<HttpOption>>(canned);

        await field.LoadDynamicOptionsAsync(fetcher, new Dictionary<string, string>(), TestContext.Current.CancellationToken);

        field.DropdownOptions.Select(o => o.Label).Should().Equal("Kiosk-01", "Kiosk-02");
        field.DropdownOptions.Select(o => o.Value).Should().Equal("uuid-1", "uuid-2");
        field.HasOptionsError.Should().BeFalse();

        // Selecting the friendly label binds its VALUE into the collected param map.
        field.SelectedOption = "uuid-2";
        field.GetStringValue().Should().Be("uuid-2");
        field.Validate().Should().BeTrue("uuid-2 is a member of the fetched options");
    }

    [Fact]
    public async Task Selected_source_value_flows_through_viewmodel_collected_params()
    {
        var parameters = new List<ParameterDefinition>
        {
            Param("app", ParameterType.Enum,
                source: new ParameterSource("https://api/apps", "data", "id", "name")),
        };
        var screens = new List<InstallerScreen>
        {
            new("configure", "Configure", null, null, new List<ScreenField> { new("app", null) }),
        };

        var vm = new InstallerViewModel(new BrandTokens { AppName = "Acme" });
        vm.ConfigureOptionsFetcher((_, _, _, _, _) => Task.FromResult<IReadOnlyList<HttpOption>>(
            new List<HttpOption> { new("Alpha", "id-a"), new("Beta", "id-b") }));
        vm.LoadScreens(screens, parameters);

        vm.Next(); // Welcome → Location
        vm.Next(); // Location → configure (custom): triggers the option fetch

        var field = vm.CurrentCustomScreen!.Fields.Single(f => f.ParamName == "app");
        // The fire-and-forget load completes on the same synchronization context;
        // give the awaited continuation a chance to run.
        await Task.Yield();
        field.DropdownOptions.Should().HaveCount(2);

        field.SelectedOption = "id-b";
        vm.CollectedParameterValues["app"].Should().Be("id-b");
    }

    // ── ${parameters.*} URL substitution before fetch ────────────────────────

    [Fact]
    public async Task Url_substitutes_parameters_placeholder_from_collected_values_before_fetch()
    {
        var def = Param("app", ParameterType.Enum,
            source: new ParameterSource(
                "https://api/tenants/${parameters.tenant}/apps?env=${parameters.env}",
                "data", "id", "name"));
        var field = new FieldViewModel(def, widgetOverride: null);

        string? seenUrl = null;
        OptionsFetcher fetcher = (url, _, _, _, _) =>
        {
            seenUrl = url;
            return Task.FromResult<IReadOnlyList<HttpOption>>(new List<HttpOption> { new("A", "a") });
        };

        var collected = new Dictionary<string, string>
        {
            ["tenant"] = "acme",
            ["env"] = "prod",
        };
        await field.LoadDynamicOptionsAsync(fetcher, collected, TestContext.Current.CancellationToken);

        seenUrl.Should().Be("https://api/tenants/acme/apps?env=prod");
    }

    [Fact]
    public void SubstituteParameters_replaces_known_and_blanks_unknown_tokens()
    {
        var url = "https://h/${parameters.known}/${parameters.missing}";
        var result = FieldViewModel.SubstituteParameters(url,
            new Dictionary<string, string> { ["known"] = "K" });
        result.Should().Be("https://h/K/");
    }

    // ── Failure handling: no crash, inline error, wizard proceeds ─────────────

    [Fact]
    public async Task Failed_fetch_sets_inline_error_and_does_not_throw()
    {
        // Optional (non-install-time) source-backed field: a failed fetch leaves it
        // empty, shows the inline error, and — since it isn't required — the user can
        // still proceed past the screen.
        var def = Param("app", ParameterType.Enum, installTime: false,
            source: new ParameterSource("https://api/apps", "data", "id", "name"));
        var field = new FieldViewModel(def, widgetOverride: null);

        OptionsFetcher throwing = (_, _, _, _, _) =>
            Task.FromException<IReadOnlyList<HttpOption>>(new System.Net.Http.HttpRequestException("boom"));

        // Must not propagate — the wizard keeps running.
        await field.LoadDynamicOptionsAsync(throwing, new Dictionary<string, string>(), TestContext.Current.CancellationToken);

        field.DropdownOptions.Should().BeEmpty();
        field.HasOptionsError.Should().BeTrue();
        field.OptionsError.Should().Be("Couldn't load options.");
        field.IsLoadingOptions.Should().BeFalse();

        // Non-required field with no selection still validates (empty is allowed) —
        // the user is not trapped by a failed dynamic load.
        field.Validate().Should().BeTrue();
    }

    [Fact]
    public async Task Failed_fetch_on_required_field_blocks_with_choose_prompt_not_a_crash()
    {
        // A required source-backed field whose fetch failed correctly blocks Next
        // (no valid option to pick) — but via inline validation, never a crash.
        var def = Param("app", ParameterType.Enum, // install-time, no default → required
            source: new ParameterSource("https://api/apps", "data", "id", "name"));
        var field = new FieldViewModel(def, widgetOverride: null);

        OptionsFetcher throwing = (_, _, _, _, _) =>
            Task.FromException<IReadOnlyList<HttpOption>>(new System.Net.Http.HttpRequestException("boom"));
        await field.LoadDynamicOptionsAsync(throwing, new Dictionary<string, string>(), TestContext.Current.CancellationToken);

        field.HasOptionsError.Should().BeTrue();
        field.Validate().Should().BeFalse();
        field.ValidationError.Should().Contain("app");
    }

    [Fact]
    public async Task Empty_fetch_result_reports_could_not_load_options()
    {
        var def = Param("app", ParameterType.Enum,
            source: new ParameterSource("https://api/apps", "data", "id", "name"));
        var field = new FieldViewModel(def, widgetOverride: null);

        OptionsFetcher empty = (_, _, _, _, _) =>
            Task.FromResult<IReadOnlyList<HttpOption>>(new List<HttpOption>());

        await field.LoadDynamicOptionsAsync(empty, new Dictionary<string, string>(), TestContext.Current.CancellationToken);

        field.DropdownOptions.Should().BeEmpty();
        field.HasOptionsError.Should().BeTrue();
    }

    [Fact]
    public async Task Reload_drops_a_stale_selection_no_longer_offered()
    {
        var def = Param("app", ParameterType.Enum,
            source: new ParameterSource("https://api/apps", "data", "id", "name"));
        var field = new FieldViewModel(def, widgetOverride: null);

        OptionsFetcher first = (_, _, _, _, _) =>
            Task.FromResult<IReadOnlyList<HttpOption>>(new List<HttpOption> { new("Alpha", "id-a") });
        await field.LoadDynamicOptionsAsync(first, new Dictionary<string, string>(), TestContext.Current.CancellationToken);
        field.SelectedOption = "id-a";

        // A second fetch (e.g. after an upstream parameter changed) no longer offers id-a.
        OptionsFetcher second = (_, _, _, _, _) =>
            Task.FromResult<IReadOnlyList<HttpOption>>(new List<HttpOption> { new("Beta", "id-b") });
        await field.LoadDynamicOptionsAsync(second, new Dictionary<string, string>(), TestContext.Current.CancellationToken);

        field.SelectedOption.Should().BeNull("the previously-selected value is no longer available");
        field.DropdownOptions.Select(o => o.Value).Should().Equal("id-b");
    }
}
