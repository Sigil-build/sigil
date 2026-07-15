using System;
using System.Collections.Generic;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Installer.Host.Branding;
using SigilBuild.Installer.Host.ViewModels;
using SigilBuild.Wrapper.Core.Localization;
using Xunit;

namespace SigilBuild.Installer.Host.Tests.Localization;

/// <summary>
/// Task 13 (P9): ViewModel + code-behind chrome routed through the catalog. These
/// mutate the process-wide <see cref="SessionLanguage"/>, so they're serialized in
/// their own collection and reset it in <see cref="Dispose"/> — this assembly's
/// other tests (e.g. bare <c>new BrandTokens()</c> expecting the English fallback)
/// must never observe a language left set by a prior test.
/// </summary>
[Collection("SessionLanguage")]
public sealed class ViewModelLocalizationTests : IDisposable
{
    public void Dispose() => SessionLanguage.ResetForTesting();

    [Fact]
    public void InstallPathError_IsLocalized()
    {
        SessionLanguage.SetForTesting(Lang.Uk);
        var vm = new InstallerViewModel(new BrandTokens()) { InstallPath = string.Empty };

        vm.ValidateDestination().Should().BeFalse();

        vm.InstallPathError.Should().Be("Укажіть розташування для встановлення.");
    }

    [Fact]
    public void RailLabels_NeverLeakEnumNamesOrScreenIds()
    {
        SessionLanguage.SetForTesting(Lang.En);
        var vm = new InstallerViewModel(new BrandTokens());

        vm.RailSteps.Should().NotContain(s => s.Label == nameof(InstallerStep.Finish));
        vm.RailSteps.Should().OnlyContain(s => !string.IsNullOrWhiteSpace(s.Label));
    }

    [Fact]
    public void DowngradeNotice_IsOneKey_NotThreeFragments()
    {
        SessionLanguage.SetForTesting(Lang.Uk);
        var text = Strings.DowngradeBody(Lang.Uk, "2.1.0", "Acme", "1.0.0");

        text.Should().StartWith("Уже встановлено новішу версію");
        text.Should().Contain("Acme");
    }

    [Fact]
    public void RailLabel_ForDeclaredScreen_PrefersManifestTitle_OverConfigureFallback()
    {
        SessionLanguage.SetForTesting(Lang.Uk);
        var vm = new InstallerViewModel(new BrandTokens());

        var screens = new List<InstallerScreen>
        {
            new(
                "db_setup",
                new LocalizedText(new Dictionary<string, string>
                {
                    ["en"] = "Database Setup",
                    ["uk"] = "Налаштування бази даних",
                }),
                null,
                null,
                Array.Empty<ScreenField>()),
        };
        vm.LoadScreens(screens, Array.Empty<ParameterDefinition>());

        vm.RailSteps.Should().Contain(s => s.Label == "Налаштування бази даних");
        vm.RailSteps.Should().NotContain(s => s.Label == "db_setup");
        vm.RailSteps.Should().NotContain(s => s.Label == "Configure");
    }

    [Fact]
    public void RebootNotice_IsLocalized()
    {
        SessionLanguage.SetForTesting(Lang.Uk);
        var vm = new InstallerViewModel(new BrandTokens());

        vm.SetRebootRequired(true);

        vm.RebootNotice.Should().Be(
            "Щоб завершити налаштування необхідного компонента, потрібно перезавантажити комп'ютер.");
    }

    [Fact]
    public void BrandFallbacks_AreLocalized()
    {
        SessionLanguage.SetForTesting(Lang.Uk);
        var tokens = new BrandTokens();

        tokens.AppName.Should().Be("Програма");
        tokens.Publisher.Should().Be("Видавець");
    }
}
